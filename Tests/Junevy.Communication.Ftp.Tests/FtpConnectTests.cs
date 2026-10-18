using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using Junevy.Communication.Core.Results;
using Junevy.Communication.Ftp.Tests.TestSupport;
using Junevy.Communication.Testing;

namespace Junevy.Communication.Ftp.Tests;

/// <summary>
/// 连接与登录（P63-D3、D14；计划 Task 3 第 2 条）。涉及真实套接字，放入 <c>SocketTiming</c> 集合。
/// </summary>
[Collection(SocketTimingCollection.Name)]
public sealed class FtpConnectTests
{
    [Fact]
    public async Task UserNameAndPassword_Login_Succeeds()
    {
        await using FakeFtpServer server = FakeFtpServer.Start();
        await using FtpClient client = FtpClient.Create(FtpTestClients.OptionsFor(server));

        CommResult result = await client.ConnectAsync();

        Assert.True(result.IsSuccess, result.ToString());
        Assert.True(client.IsConnected);
        Assert.Contains(server.Commands.Snapshot(), c => c.Verb == "USER" && c.Argument == "line1");
    }

    [Fact]
    public async Task Anonymous_Login_Succeeds_WhenTheServerAllowsIt()
    {
        await using FakeFtpServer server = FakeFtpServer.Start(new FakeFtpOptions { AllowAnonymous = true });
        FtpClientOptions options = FtpTestClients.OptionsFor(server, userName: null, password: null);
        await using FtpClient client = FtpClient.Create(options);

        CommResult result = await client.ConnectAsync();

        Assert.True(result.IsSuccess, result.ToString());
        Assert.True(client.IsConnected);
    }

    [Fact]
    public async Task WrongPassword_IsAuthenticationFailed_WithCode530()
    {
        await using FakeFtpServer server = FakeFtpServer.Start();
        await using FtpClient client = FtpClient.Create(FtpTestClients.OptionsFor(server, password: "wrong"));

        CommResult result = await client.ConnectAsync();

        Assert.False(result.IsSuccess);
        Assert.Equal(CommErrorKind.AuthenticationFailed, result.ErrorKind);
        Assert.Equal(530, result.ProtocolErrorCode);
        Assert.False(client.IsConnected);
    }

    [Fact]
    public async Task NoListener_IsConnectionClosed()
    {
        int port = FreeLoopbackPort();
        await using FtpClient client = FtpClient.Create(new FtpClientOptions { Host = LoopbackTcp.Host, Port = port });

        CommResult result = await client.ConnectAsync();

        Assert.Equal(CommErrorKind.ConnectionClosed, result.ErrorKind);
        Assert.False(client.IsConnected);
    }

    [Fact]
    public async Task SilentServer_IsTimeout_WithinTheReadTimeout()
    {
        using SilentTcpServer silent = SilentTcpServer.Start();
        const int readTimeout = 300;
        await using FtpClient client = FtpClient.Create(new FtpClientOptions
        {
            Host = LoopbackTcp.Host,
            Port = silent.Port,
            ReadTimeout = readTimeout,
        });
        var stopwatch = Stopwatch.StartNew();

        CommResult result = await client.ConnectAsync();

        stopwatch.Stop();
        Assert.Equal(CommErrorKind.Timeout, result.ErrorKind);
        // 区间断言（计划第 1 节第 5 条）：下限取期望值的 80%，上限加 2000 ms。
        Assert.InRange(stopwatch.ElapsedMilliseconds, readTimeout * 4 / 5, readTimeout + 2000);
    }

    [Fact]
    public async Task CallerCancelled_IsCancelled()
    {
        using SilentTcpServer silent = SilentTcpServer.Start();
        await using FtpClient client = FtpClient.Create(new FtpClientOptions { Host = LoopbackTcp.Host, Port = silent.Port });
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(200));

        CommResult result = await client.ConnectAsync(cancellation.Token);

        Assert.Equal(CommErrorKind.Cancelled, result.ErrorKind);
    }

    [Fact]
    public async Task AlreadyCancelledToken_IsCancelled_WithoutConnecting()
    {
        await using FakeFtpServer server = FakeFtpServer.Start();
        await using FtpClient client = FtpClient.Create(FtpTestClients.OptionsFor(server));

        CommResult result = await client.ConnectAsync(new CancellationToken(canceled: true));

        Assert.Equal(CommErrorKind.Cancelled, result.ErrorKind);
        Assert.Equal(0, server.ConnectionCount);
    }

    [Fact]
    public async Task ConnectTwice_ReusesTheControlConnection()
    {
        await using FakeFtpServer server = FakeFtpServer.Start();
        await using FtpClient client = FtpClient.Create(FtpTestClients.OptionsFor(server));

        CommResult first = await client.ConnectAsync();
        CommResult second = await client.ConnectAsync();

        Assert.True(first.IsSuccess, first.ToString());
        Assert.True(second.IsSuccess, second.ToString());
        Assert.Equal(1, server.ConnectionCount);
    }

    private static int FreeLoopbackPort()
    {
        var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        int port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        return port;
    }
}
