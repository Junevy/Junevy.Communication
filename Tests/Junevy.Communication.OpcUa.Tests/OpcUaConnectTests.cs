using Junevy.Communication.Core.Results;
using Junevy.Communication.OpcUa.Tests.TestSupport;
using Microsoft.Extensions.Logging;
using Xunit;

namespace Junevy.Communication.OpcUa.Tests;

/// <summary>
/// 连接、断开、状态与释放（计划第 7 节第 2 条）。需要真实套接字，因此放进 <c>SocketTiming</c> 集合。
/// </summary>
[Collection(SocketTimingCollection.Name)]
public sealed class OpcUaConnectTests
{
    private readonly ILoggerFactory loggerFactory;

    public OpcUaConnectTests()
        => loggerFactory = LoggerFactory.Create(builder => builder.SetMinimumLevel(LogLevel.Warning));

    private OpcUaClientOptions OptionsFor(string endpointUrl) => new OpcUaClientOptions
    {
        EndpointUrl = endpointUrl,
        PkiRoot = OpcUaTestServerFactory.UniquePkiRoot(),
        ConnectTimeout = 10000,
        OperationTimeout = 5000,
        SessionTimeout = 30000,
        KeepAliveInterval = 1000,
        ReconnectInterval = 200,
        MaxReconnectInterval = 1000,
    };

    [Fact]
    public async Task Connect_Succeeds_AndWalksDisconnectedConnectingConnected()
    {
        await using OpcUaTestServer server = await OpcUaTestServerFactory.StartOwnAsync(loggerFactory: loggerFactory);
        await using OpcUaClient client = OpcUaClient.Create(OptionsFor(server.EndpointUrl), loggerFactory);

        Assert.Equal(OpcUaSessionState.Disconnected, client.State);

        var states = new List<OpcUaSessionState>();
        client.StateChanged += (_, args) => states.Add(args.Current);

        CommResult result = await client.ConnectAsync();

        Assert.True(result.IsSuccess, result.ToString());
        Assert.Equal(OpcUaSessionState.Connected, client.State);
        Assert.Equal(new[] { OpcUaSessionState.Connecting, OpcUaSessionState.Connected }, states);
    }

    [Fact]
    public async Task Connect_AlreadyConnected_ReturnsSuccessWithoutReconnecting()
    {
        await using OpcUaTestServer server = await OpcUaTestServerFactory.StartOwnAsync(loggerFactory: loggerFactory);
        await using OpcUaClient client = OpcUaClient.Create(OptionsFor(server.EndpointUrl), loggerFactory);

        Assert.True((await client.ConnectAsync()).IsSuccess);
        Assert.True((await client.ConnectAsync()).IsSuccess);
        Assert.Equal(OpcUaSessionState.Connected, client.State);
    }

    [Fact]
    public async Task Connect_ServerWithoutNoneEndpoint_ReturnsNotSupported()
    {
        await using OpcUaTestServer server = await OpcUaTestServerFactory.StartOwnAsync(
            options => options.IncludeUnsecurePolicyNone = false,
            loggerFactory);

        await using OpcUaClient client = OpcUaClient.Create(OptionsFor(server.EndpointUrl), loggerFactory);

        CommResult result = await client.ConnectAsync();

        Assert.False(result.IsSuccess);
        Assert.Equal(CommErrorKind.NotSupported, result.ErrorKind);
        Assert.Contains("None security policy", result.ErrorMessage, StringComparison.Ordinal);
        Assert.Equal(OpcUaSessionState.Disconnected, client.State);
    }

    [Fact]
    public async Task Connect_ServerWithoutAnonymous_ReturnsNotSupported()
    {
        await using OpcUaTestServer server = await OpcUaTestServerFactory.StartOwnAsync(
            options => options.AllowAnonymous = false,
            loggerFactory);

        await using OpcUaClient client = OpcUaClient.Create(OptionsFor(server.EndpointUrl), loggerFactory);

        CommResult result = await client.ConnectAsync();

        Assert.False(result.IsSuccess);
        Assert.Equal(CommErrorKind.NotSupported, result.ErrorKind);
        Assert.Contains("anonymous", result.ErrorMessage, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Connect_EndpointReportsAnUnresolvableHost_StillConnectsToTheConfiguredAddress()
    {
        // P62-D3 ④：报告 localhost 证明不了替换生效，因为 localhost 本来就能解析。
        await using OpcUaTestServer server = await OpcUaTestServerFactory.StartOwnAsync(
            options => options.ReportedHostName = "opcua-test.invalid",
            loggerFactory);

        await using OpcUaClient client = OpcUaClient.Create(OptionsFor(server.EndpointUrl), loggerFactory);

        CommResult result = await client.ConnectAsync();

        Assert.True(result.IsSuccess, result.ToString());
        Assert.Contains("opcua-test.invalid", server.AdvertisedUrl, StringComparison.Ordinal);
        Assert.Equal(OpcUaSessionState.Connected, client.State);
    }

    [Fact]
    public async Task Connect_NothingListening_ReturnsConnectionClosed()
    {
        OpcUaClientOptions options = OptionsFor(OpcUaTestServerFactory.FreeEndpointUrl());
        await using OpcUaClient client = OpcUaClient.Create(options, loggerFactory);

        CommResult result = await client.ConnectAsync();

        Assert.False(result.IsSuccess);
        Assert.Contains(
            result.ErrorKind,
            new[] { CommErrorKind.ConnectionClosed, CommErrorKind.Timeout });
        Assert.Equal(OpcUaSessionState.Disconnected, client.State);
    }

    [Fact]
    public async Task Connect_ServerAcceptsButNeverAnswers_ReportsTimeoutWithinTheConfiguredWindow()
    {
        using var silent = new SilentOpcUaPort();
        OpcUaClientOptions options = OptionsFor(silent.EndpointUrl);
        options.ConnectTimeout = 1500;
        await using OpcUaClient client = OpcUaClient.Create(options, loggerFactory);

        DateTime started = DateTime.UtcNow;
        CommResult result = await client.ConnectAsync();
        TimeSpan elapsed = DateTime.UtcNow - started;

        Assert.False(result.IsSuccess);
        Assert.Equal(CommErrorKind.Timeout, result.ErrorKind);
        Assert.InRange(elapsed.TotalMilliseconds, 500, 15000);
    }

    [Fact]
    public async Task Connect_WhileAnotherConnectIsInProgress_ReturnsInvalidRequest()
    {
        using var silent = new SilentOpcUaPort();
        OpcUaClientOptions options = OptionsFor(silent.EndpointUrl);
        options.ConnectTimeout = 10000;
        await using OpcUaClient client = OpcUaClient.Create(options, loggerFactory);

        Task<CommResult> first = client.ConnectAsync();
        await WaitUntilAsync(() => client.State == OpcUaSessionState.Connecting);
        try
        {
            CommResult second = await client.ConnectAsync();

            Assert.False(second.IsSuccess);
            Assert.Equal(CommErrorKind.InvalidRequest, second.ErrorKind);
            Assert.Equal(OpcUaSessionState.Connecting, client.State);
        }
        finally
        {
            await first;
        }
    }

    private static async Task WaitUntilAsync(Func<bool> condition, int timeoutMilliseconds = 5000)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMilliseconds);
        while (DateTime.UtcNow < deadline)
        {
            if (condition())
            {
                return;
            }

            await Task.Delay(20);
        }

        Assert.Fail("The expected state was not reached in time.");
    }

    [Fact]
    public async Task Connect_CallerCancels_ReportsCancelled()
    {
        using var silent = new SilentOpcUaPort();
        OpcUaClientOptions options = OptionsFor(silent.EndpointUrl);
        options.ConnectTimeout = 30000;
        await using OpcUaClient client = OpcUaClient.Create(options, loggerFactory);

        using CancellationTokenSource cancellation = new();
        cancellation.CancelAfter(300);

        CommResult result = await client.ConnectAsync(cancellation.Token);

        Assert.False(result.IsSuccess);
        Assert.Equal(CommErrorKind.Cancelled, result.ErrorKind);
    }

    [Fact]
    public async Task Connect_CallerGivesUpWhileTheServerStillAnswers_LeavesNoSessionBehind()
    {
        await using OpcUaTestServer server = await OpcUaTestServerFactory.StartOwnAsync(loggerFactory: loggerFactory);
        await using OpcUaClient client = OpcUaClient.Create(OptionsFor(server.EndpointUrl), loggerFactory);

        // 调用方先放弃：令牌在调用前就已取消，调用立刻返回 Cancelled。
        // SDK 那条后台连接不理令牌（F23），照样会在服务器上建出一个会话。
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        CommResult result = await client.ConnectAsync(cancelled.Token);
        Assert.Equal(CommErrorKind.Cancelled, result.ErrorKind);

        // 那个迟到的会话必须当场关掉：不能挂到客户端上，也不能留在服务器上。
        await WaitUntilAsync(() => server.ActivatedSessions >= 1, 15000);
        await WaitUntilAsync(() => server.SessionCount == 0, 15000);
        Assert.Equal(OpcUaSessionState.Disconnected, client.State);

        // 之后正常连接只有一个会话，断开后一个不剩。
        Assert.True((await client.ConnectAsync()).IsSuccess);
        Assert.Equal(1, server.SessionCount);
        Assert.True((await client.DisconnectAsync()).IsSuccess);
        Assert.Equal(0, server.SessionCount);
    }

    [Fact]
    public async Task Disconnect_ClosesTheSessionOnTheServer_AndCanBeRepeated()
    {
        await using OpcUaTestServer server = await OpcUaTestServerFactory.StartOwnAsync(loggerFactory: loggerFactory);
        await using OpcUaClient client = OpcUaClient.Create(OptionsFor(server.EndpointUrl), loggerFactory);

        Assert.True((await client.ConnectAsync()).IsSuccess);
        Assert.Equal(1, server.SessionCount);

        Assert.True((await client.DisconnectAsync()).IsSuccess);
        Assert.Equal(OpcUaSessionState.Disconnected, client.State);
        Assert.Equal(0, server.SessionCount);

        Assert.True((await client.DisconnectAsync()).IsSuccess);
        Assert.Equal(OpcUaSessionState.Disconnected, client.State);
    }

    [Fact]
    public async Task Disconnect_BeforeConnecting_Succeeds()
    {
        await using OpcUaTestServer server = await OpcUaTestServerFactory.StartOwnAsync(loggerFactory: loggerFactory);
        await using OpcUaClient client = OpcUaClient.Create(OptionsFor(server.EndpointUrl), loggerFactory);

        Assert.True((await client.DisconnectAsync()).IsSuccess);
        Assert.Equal(OpcUaSessionState.Disconnected, client.State);
    }

    [Fact]
    public async Task StateChanged_ReportsPreviousCurrentAndReason()
    {
        await using OpcUaTestServer server = await OpcUaTestServerFactory.StartOwnAsync(loggerFactory: loggerFactory);
        await using OpcUaClient client = OpcUaClient.Create(OptionsFor(server.EndpointUrl), loggerFactory);

        var changes = new List<OpcUaSessionStateChangedEventArgs>();
        client.StateChanged += (_, args) => changes.Add(args);

        Assert.True((await client.ConnectAsync()).IsSuccess);
        Assert.True((await client.DisconnectAsync()).IsSuccess);

        Assert.Equal(3, changes.Count);
        Assert.Equal(OpcUaSessionState.Disconnected, changes[0].Previous);
        Assert.Equal(OpcUaSessionState.Connecting, changes[0].Current);
        Assert.Equal(OpcUaSessionState.Connecting, changes[1].Previous);
        Assert.Equal(OpcUaSessionState.Connected, changes[1].Current);
        Assert.Equal(OpcUaSessionState.Connected, changes[2].Previous);
        Assert.Equal(OpcUaSessionState.Disconnected, changes[2].Current);
    }

    [Fact]
    public async Task StateChanged_SubscriberThatThrows_DoesNotBreakTheStateOrTheOtherSubscribers()
    {
        await using OpcUaTestServer server = await OpcUaTestServerFactory.StartOwnAsync(loggerFactory: loggerFactory);
        await using OpcUaClient client = OpcUaClient.Create(OptionsFor(server.EndpointUrl), loggerFactory);

        var seen = new List<OpcUaSessionState>();
        client.StateChanged += (_, _) => throw new InvalidOperationException("subscriber blew up");
        client.StateChanged += (_, args) => seen.Add(args.Current);

        CommResult result = await client.ConnectAsync();

        Assert.True(result.IsSuccess, result.ToString());
        Assert.Equal(OpcUaSessionState.Connected, client.State);
        Assert.Equal(new[] { OpcUaSessionState.Connecting, OpcUaSessionState.Connected }, seen);
    }

    [Fact]
    public async Task Create_TakesASnapshotOfTheOptions()
    {
        await using OpcUaTestServer server = await OpcUaTestServerFactory.StartOwnAsync(loggerFactory: loggerFactory);
        OpcUaClientOptions options = OptionsFor(server.EndpointUrl);
        await using OpcUaClient client = OpcUaClient.Create(options, loggerFactory);

        options.EndpointUrl = OpcUaTestServerFactory.FreeEndpointUrl();

        Assert.True((await client.ConnectAsync()).IsSuccess);
    }

    [Fact]
    public void Create_NullOptions_Throws()
        => Assert.Throws<ArgumentNullException>(() => OpcUaClient.Create(null!));

    [Fact]
    public void Create_InvalidOptions_ThrowsWithThePropertyName()
    {
        var error = Assert.Throws<ArgumentException>(() => OpcUaClient.Create(new OpcUaClientOptions { EndpointUrl = "http://x/" }));

        Assert.Equal("options.EndpointUrl", error.ParamName);
    }

    [Fact]
    public void ToString_MentionsTheEndpointAndTheState()
    {
        using OpcUaClient client = OpcUaClient.Create(new OpcUaClientOptions { EndpointUrl = "opc.tcp://127.0.0.1:1/" });

        Assert.Equal("OpcUaClient(opc.tcp://127.0.0.1:1/, Disconnected)", client.ToString());
    }
}
