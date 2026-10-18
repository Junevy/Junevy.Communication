using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using Junevy.Communication.Core.Results;
using Junevy.Communication.Testing;
using Junevy.Communication.WebApi.Server.Config;
using Junevy.Communication.WebApi.Server.Tests.TestSupport;
using Microsoft.Extensions.Logging;
using Xunit;

namespace Junevy.Communication.WebApi.Server.Tests;

/// <summary>
/// 公开外观的生命周期（P32-D4、D12、D13）：状态机与事件顺序、重复启动与无路由、端口占用与无权绑定的分类、停止的期限、释放的规则、
/// 环境变量与工作目录下的 appsettings 不影响服务器（Q29），以及通配地址未认证时的警告。
/// </summary>
[Collection(SocketTimingCollection.Name)]
public sealed class WebApiServerLifecycleTests
{
    private const string Loopback = "http://127.0.0.1:0";

    [Fact]
    public async Task Urls_AreTheConfiguredAddresses_BeforeStart_AndTheBoundAddresses_AfterStart()
    {
        await using var server = CreateServer(Loopback, out _);
        Assert.Equal(new[] { Loopback }, server.Urls);
        server.MapGet<Order>("/x", (_, _) => Task.FromResult(WebApiServerResponse<Order>.Ok(new Order())));

        CommResult result = await server.StartAsync();

        Assert.True(result.IsSuccess);
        string bound = Assert.Single(server.Urls);
        Assert.StartsWith("http://127.0.0.1:", bound, StringComparison.Ordinal);
        Assert.NotEqual(Loopback, bound);
    }

    [Fact]
    public async Task StateChanged_FollowsTheLifecycleInOrder()
    {
        await using var server = CreateServer(Loopback, out _);
        server.MapGet<Order>("/x", (_, _) => Task.FromResult(WebApiServerResponse<Order>.Ok(new Order())));
        var states = new List<WebApiServerState>();
        server.StateChanged += (_, args) =>
        {
            lock (states)
                states.Add(args.Current);
        };

        await server.StartAsync();
        await server.StopAsync();

        lock (states)
            Assert.Equal(new[] { WebApiServerState.Starting, WebApiServerState.Running, WebApiServerState.Stopping, WebApiServerState.Stopped }, states);
    }

    [Fact]
    public async Task StartingTwice_IsInvalidRequest()
    {
        await using var server = CreateServer(Loopback, out _);
        server.MapGet<Order>("/x", (_, _) => Task.FromResult(WebApiServerResponse<Order>.Ok(new Order())));
        await server.StartAsync();

        CommResult second = await server.StartAsync();

        Assert.Equal(CommErrorKind.InvalidRequest, second.ErrorKind);
        Assert.Equal(WebApiServerState.Running, server.State);
    }

    [Fact]
    public async Task StartingWithoutRoutes_IsInvalidRequest_AndRoutesCanStillBeAdded()
    {
        await using var server = CreateServer(Loopback, out _);

        CommResult result = await server.StartAsync();
        server.MapGet<Order>("/later", (_, _) => Task.FromResult(WebApiServerResponse<Order>.Ok(new Order())));

        Assert.Equal(CommErrorKind.InvalidRequest, result.ErrorKind);
        Assert.Equal(WebApiServerState.Stopped, server.State);
        Assert.True((await server.StartAsync()).IsSuccess);
    }

    [Fact]
    public async Task InvalidRouteTemplate_IsInvalidRequest_AndTheServerStaysStopped()
    {
        await using var server = CreateServer(Loopback, out _);
        server.MapGet<Order>("/orders/{", (_, _) => Task.FromResult(WebApiServerResponse<Order>.Ok(new Order())));

        CommResult result = await server.StartAsync();

        Assert.Equal(CommErrorKind.InvalidRequest, result.ErrorKind);
        Assert.Equal(WebApiServerState.Stopped, server.State);
    }

    [Fact]
    public async Task PortInUse_IsResourceExhausted_AndTheServerReturnsToStopped()
    {
        using var occupant = new TcpListener(IPAddress.Loopback, 0);
        occupant.Start();
        int port = ((IPEndPoint)occupant.LocalEndpoint).Port;
        await using var server = CreateServer($"http://127.0.0.1:{port}", out _);
        server.MapGet<Order>("/x", (_, _) => Task.FromResult(WebApiServerResponse<Order>.Ok(new Order())));

        CommResult result = await server.StartAsync();

        Assert.Equal(CommErrorKind.ResourceExhausted, result.ErrorKind);
        Assert.Equal(WebApiServerState.Stopped, server.State);
        Assert.Equal($"http://127.0.0.1:{port}", Assert.Single(server.Urls));
    }

    [Fact]
    public async Task ExcludedPort_IsAuthenticationFailed_OnWindows()
    {
        if (!OperatingSystem.IsWindows() || !IsReservedPortOnThisMachine(5357))
            return;

        await using var server = CreateServer("http://127.0.0.1:5357", out _);
        server.MapGet<Order>("/x", (_, _) => Task.FromResult(WebApiServerResponse<Order>.Ok(new Order())));

        CommResult result = await server.StartAsync();

        Assert.Equal(CommErrorKind.AuthenticationFailed, result.ErrorKind);
        Assert.Equal(WebApiServerState.Stopped, server.State);
    }

    [Fact]
    public async Task StopThenStart_ServesRequestsAgain()
    {
        await using var server = CreateServer(Loopback, out _);
        server.MapGet<Order>("/x", (_, _) => Task.FromResult(WebApiServerResponse<Order>.Ok(new Order())));
        await server.StartAsync();
        await server.StopAsync();

        CommResult again = await server.StartAsync();
        using var http = new HttpClient();
        HttpResponseMessage response = await http.GetAsync(server.Urls[0] + "/x");

        Assert.True(again.IsSuccess);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task StopTimeout_AbortsTheInFlightRequest_AndCancelsTheHandlerToken()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var sawAbort = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var server = CreateServer(Loopback, out _, config => config.StopTimeout = 300);
        server.MapGet<Order>("/slow", async (_, token) =>
        {
            entered.TrySetResult();
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(10), token);
            }
            catch (OperationCanceledException)
            {
                sawAbort.TrySetResult();
                throw;
            }

            return WebApiServerResponse<Order>.Ok(new Order());
        });
        await server.StartAsync();
        using var http = new HttpClient();
        Task<HttpResponseMessage> inFlight = http.GetAsync(server.Urls[0] + "/slow");
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));

        var clock = Stopwatch.StartNew();
        await server.StopAsync();
        clock.Stop();

        Assert.InRange(clock.Elapsed, TimeSpan.FromMilliseconds(250), TimeSpan.FromSeconds(3));
        await sawAbort.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(WebApiServerState.Stopped, server.State);
        await Record.ExceptionAsync(() => inFlight);
    }

    [Fact]
    public async Task HandlerIgnoringTheToken_DoesNotBlockStopOrDispose_AndLeavesNoUnobservedException()
    {
        using var unobserved = new UnobservedTaskWatch();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var server = CreateServer(Loopback, out _, config => config.StopTimeout = 300);
        server.MapGet<Order>("/stubborn", async (_, _) =>
        {
            entered.TrySetResult();
            await Task.Delay(TimeSpan.FromMilliseconds(1500));
            throw new InvalidOperationException("late failure");
        });
        await server.StartAsync();
        using var http = new HttpClient();
        Task<HttpResponseMessage> inFlight = http.GetAsync(server.Urls[0] + "/stubborn");
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));

        var clock = Stopwatch.StartNew();
        await server.StopAsync();
        await server.DisposeAsync();
        clock.Stop();

        Assert.InRange(clock.Elapsed, TimeSpan.FromMilliseconds(250), TimeSpan.FromSeconds(2));
        await Task.Delay(TimeSpan.FromMilliseconds(1700));
        await unobserved.AssertNoneAsync();
        await Record.ExceptionAsync(() => inFlight);
    }

    [Fact]
    public async Task Dispose_IsRepeatable_AndAfterwardsStartAndMapThrowObjectDisposed_WhileStopIsANoOp()
    {
        WebApiServer server = CreateServer(Loopback, out _);
        server.MapGet<Order>("/x", (_, _) => Task.FromResult(WebApiServerResponse<Order>.Ok(new Order())));
        await server.StartAsync();

        await server.DisposeAsync();
        await server.DisposeAsync();
        server.Dispose();

        Assert.Throws<ObjectDisposedException>(() => { _ = server.StartAsync(); });
        Assert.Throws<ObjectDisposedException>(() => server.MapGet<Order>("/y", (_, _) => Task.FromResult(WebApiServerResponse<Order>.Ok(new Order()))));
        await server.StopAsync();
        Assert.Equal(WebApiServerState.Stopped, server.State);
    }

    [Fact]
    public async Task MapAfterStart_IsInvalidOperation()
    {
        await using var server = CreateServer(Loopback, out _);
        server.MapGet<Order>("/x", (_, _) => Task.FromResult(WebApiServerResponse<Order>.Ok(new Order())));
        await server.StartAsync();

        Assert.Throws<InvalidOperationException>(() => server.MapGet<Order>("/y", (_, _) => Task.FromResult(WebApiServerResponse<Order>.Ok(new Order()))));
    }

    [Fact]
    public async Task DuplicateRoute_IsAnArgumentException()
    {
        await using var server = CreateServer(Loopback, out _);
        server.MapGet<Order>("/x", (_, _) => Task.FromResult(WebApiServerResponse<Order>.Ok(new Order())));

        Assert.Throws<ArgumentException>(() => server.MapGet<Order>("/x", (_, _) => Task.FromResult(WebApiServerResponse<Order>.Ok(new Order()))));
        Assert.Throws<ArgumentException>(() => server.MapGet<Order>(string.Empty, (_, _) => Task.FromResult(WebApiServerResponse<Order>.Ok(new Order()))));
    }

    [Theory]
    [InlineData("/X")]
    [InlineData("x")]
    [InlineData("/x/")]
    public async Task TemplateEquivalentForRouting_IsADuplicate_NotAnAmbiguityAtRequestTime(string equivalent)
    {
        await using var server = CreateServer(Loopback, out _);
        server.MapGet<Order>("/x", (_, _) => Task.FromResult(WebApiServerResponse<Order>.Ok(new Order())));

        Assert.Throws<ArgumentException>(() => server.MapGet<Order>(equivalent, (_, _) => Task.FromResult(WebApiServerResponse<Order>.Ok(new Order()))));
        server.MapPost<Order, Order>(equivalent, (order, _, _) => Task.FromResult(WebApiServerResponse<Order>.Ok(order)));
    }

    [Fact]
    public void EmptyName_IsAnArgumentException()
    {
        Assert.Throws<ArgumentException>(() => new WebApiServer(string.Empty, new WebApiServerConfig { Urls = new[] { Loopback } }));
    }

    [Fact]
    public async Task EnvironmentVariablesAndAppSettingsInTheWorkingDirectory_DoNotChangeTheServer()
    {
        string previousDirectory = Environment.CurrentDirectory;
        string? previousUrls = Environment.GetEnvironmentVariable("ASPNETCORE_URLS");
        string directory = Path.Combine(Path.GetTempPath(), "junevy-webapi-server-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            // Kestrel:Endpoints 是显式 UseUrls 盖不住的配置：只要应用读取了工作目录的 appsettings.json，它就会改变绑定的地址。
            File.WriteAllText(
                Path.Combine(directory, "appsettings.json"),
                "{ \"Urls\": \"http://127.0.0.1:5998\", \"Kestrel\": { \"Endpoints\": { \"Host\": { \"Url\": \"http://127.0.0.1:5997\" } } } }");
            Environment.CurrentDirectory = directory;
            Environment.SetEnvironmentVariable("ASPNETCORE_URLS", "http://127.0.0.1:5999");
            await using var server = CreateServer(Loopback, out _);
            server.MapGet<Order>("/x", (_, _) => Task.FromResult(WebApiServerResponse<Order>.Ok(new Order())));

            await server.StartAsync();

            string bound = Assert.Single(server.Urls);
            Assert.StartsWith("http://127.0.0.1:", bound, StringComparison.Ordinal);
            Assert.DoesNotContain(":5997", bound, StringComparison.Ordinal);
            Assert.DoesNotContain(":5998", bound, StringComparison.Ordinal);
            Assert.DoesNotContain(":5999", bound, StringComparison.Ordinal);
        }
        finally
        {
            Environment.CurrentDirectory = previousDirectory;
            Environment.SetEnvironmentVariable("ASPNETCORE_URLS", previousUrls);
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task FrameworkDebugLogs_AreNotForwarded_ToTheCallersLogger()
    {
        await using var server = CreateServer(Loopback, out TestLogger logger);
        server.MapGet<Order>("/x", (_, _) => Task.FromResult(WebApiServerResponse<Order>.Ok(new Order())));
        await server.StartAsync();
        int port = new Uri(server.Urls[0]).Port;

        using (var tcp = new TcpClient())
        {
            await tcp.ConnectAsync(IPAddress.Loopback, port);
            await tcp.GetStream().WriteAsync(Encoding.ASCII.GetBytes("this is not an HTTP request\r\n\r\n"));
            await Task.Delay(300);
        }

        Assert.DoesNotContain(logger.Entries, entry => entry.CategoryName.StartsWith("Microsoft.AspNetCore", StringComparison.Ordinal) && entry.Level < LogLevel.Warning);
    }

    [Fact]
    public async Task WildcardAddress_WithoutAuthentication_LogsAWarning()
    {
        await using var server = CreateServer("http://*:0", out TestLogger logger);
        server.MapGet<Order>("/x", (_, _) => Task.FromResult(WebApiServerResponse<Order>.Ok(new Order())));

        CommResult result = await server.StartAsync();

        Assert.True(result.IsSuccess);
        Assert.Contains(logger.GetEntries(LogLevel.Warning), entry => entry.Message.Contains("without authentication", StringComparison.Ordinal));
        await server.StopAsync();
    }

    private static WebApiServer CreateServer(string url, out TestLogger logger, Action<WebApiServerConfig>? configure = null)
    {
        logger = new TestLogger();
        var config = new WebApiServerConfig { Urls = new[] { url } };
        configure?.Invoke(config);
        return new WebApiServer("test", config, null, logger);
    }

    // 只在确实被排除的端口上运行 Windows 专用用例：绑定成功说明本机没有这条排除，用例跳过。
    private static bool IsReservedPortOnThisMachine(int port)
    {
        try
        {
            using var probe = new TcpListener(IPAddress.Loopback, port);
            probe.Start();
            probe.Stop();
            return false;
        }
        catch (SocketException ex) when (ex.SocketErrorCode == SocketError.AccessDenied)
        {
            return true;
        }
    }
}
