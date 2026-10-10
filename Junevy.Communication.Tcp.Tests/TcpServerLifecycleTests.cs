using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using Junevy.Communication.Channels;
using Junevy.Communication.Core.Results;
using static Junevy.Communication.Tcp.Tests.ServerTestHelpers;
using static Junevy.Communication.Tcp.Tests.TcpTestHelpers;

namespace Junevy.Communication.Tcp.Tests;

/// <summary>
/// TCP 服务端的启动、停止、端口占用、监听器故障与重新监听（计划 11.3）。使用真实套接字，时间相关断言使用区间（计划第 1 节第 5 条）。
/// </summary>
[Collection(SocketTimingCollection.Name)]
public sealed class TcpServerLifecycleTests
{
    [Fact(Timeout = 30000)]
    public async Task Start_PortInUse_ReturnsResourceExhausted()
    {
        int port = FreePort();
        var occupier = new TcpListener(IPAddress.Loopback, port);
        occupier.Start();
        try
        {
            await using var server = new TcpServer(CreateServerConfig(port));

            CommResult result = await WithinAsync(server.StartAsync(), 10000);

            Assert.False(result.IsSuccess);
            Assert.Equal(CommErrorKind.ResourceExhausted, result.ErrorKind);
            Assert.Equal(ServerState.Stopped, server.State);
            Assert.Null(server.LocalEndPoint);
        }
        finally
        {
            occupier.Stop();
        }
    }

    [Fact(Timeout = 30000)]
    public async Task Start_Stop_Restart()
    {
        int port = FreePort();
        await using var server = new TcpServer(CreateServerConfig(port));
        var recorder = new ServerRecorder(server);

        Assert.True((await WithinAsync(server.StartAsync(), 10000)).IsSuccess);
        Assert.Equal(ServerState.Running, server.State);
        Assert.Equal(port, server.LocalEndPoint?.Port);

        await WithinAsync(server.StopAsync(), 10000);
        Assert.Equal(ServerState.Stopped, server.State);
        Assert.Null(server.LocalEndPoint);

        Assert.True((await WithinAsync(server.StartAsync(), 10000)).IsSuccess);
        await using var client = CreateClient(port);
        Assert.True((await WithinAsync(client.ConnectAsync(), 10000)).IsSuccess);
        await WaitUntilAsync(() => recorder.ConnectedCount == 1, 5000);

        // 状态事件按顺序派发：启动、运行、停止中、已停止，再次启动并运行。
        await WaitUntilAsync(() => recorder.States.Count == 6, 5000);
        Assert.Equal(
            new[] { ServerState.Starting, ServerState.Running, ServerState.Stopping, ServerState.Stopped, ServerState.Starting, ServerState.Running },
            recorder.States);
    }

    [Fact(Timeout = 30000)]
    public async Task Stop_WithActiveSessions_WithinStopTimeout()
    {
        int port = FreePort();
        var config = CreateServerConfig(port);
        config.StopTimeout = 3000;
        await using var server = new TcpServer(config);
        var recorder = new ServerRecorder(server);
        Assert.True((await WithinAsync(server.StartAsync(), 10000)).IsSuccess);

        var clients = new List<TcpClientChannel>();
        for (int i = 0; i < 10; i++)
        {
            TcpClientChannel client = CreateClient(port);
            clients.Add(client);
            Assert.True((await WithinAsync(client.ConnectAsync(), 10000)).IsSuccess);
        }

        await WaitUntilAsync(() => recorder.ConnectedCount == 10, 10000);

        var stopwatch = Stopwatch.StartNew();
        await WithinAsync(server.StopAsync(), 10000);
        stopwatch.Stop();

        Assert.True(stopwatch.ElapsedMilliseconds < config.StopTimeout, $"StopAsync took {stopwatch.ElapsedMilliseconds} ms.");
        Assert.Equal(ServerState.Stopped, server.State);
        Assert.Equal(0, server.SessionCount);
        foreach (TcpClientChannel client in clients)
            await WaitUntilAsync(() => !client.IsConnected, 5000);

        await DisposeAllAsync(clients);
    }

    [Fact(Timeout = 30000)]
    public async Task ListenerFault_RestartOnFault_Recovers()
    {
        int port = FreePort();
        var config = CreateServerConfig(port);
        config.RestartOnFault = new ReconnectOptions { Enabled = true, Mode = ReconnectMode.FixedInterval, Interval = 100 };
        await using var server = new TcpServer(config);
        var recorder = new ServerRecorder(server);
        Assert.True((await WithinAsync(server.StartAsync(), 10000)).IsSuccess);

        server.SimulateListenerFault();

        await WaitUntilAsync(() => recorder.States.Count == 4, 5000);
        Assert.Equal(
            new[] { ServerState.Starting, ServerState.Running, ServerState.Faulted, ServerState.Running },
            recorder.States);
        Assert.Equal(ServerState.Running, server.State);

        await using var client = CreateClient(port);
        Assert.True((await WithinAsync(client.ConnectAsync(), 10000)).IsSuccess);
        await WaitUntilAsync(() => recorder.ConnectedCount == 1, 5000);
    }

    [Fact(Timeout = 30000)]
    public async Task ListenerFault_WithoutRestart_StaysFaulted()
    {
        int port = FreePort();
        await using var server = new TcpServer(CreateServerConfig(port));
        var recorder = new ServerRecorder(server);
        Assert.True((await WithinAsync(server.StartAsync(), 10000)).IsSuccess);

        server.SimulateListenerFault();

        await WaitUntilAsync(() => recorder.States.Count == 3, 5000);
        await Task.Delay(300);
        Assert.Equal(ServerState.Faulted, server.State);
        Assert.Equal(3, recorder.States.Count);
    }

    [Fact(Timeout = 30000)]
    public async Task SendToUnknownSession_ReturnsNotConnected()
    {
        await using var server = new TcpServer(CreateServerConfig(FreePort()));

        CommResult result = await server.SendAsync(42, Ascii("X"));

        Assert.False(result.IsSuccess);
        Assert.Equal(CommErrorKind.NotConnected, result.ErrorKind);
    }

    [Fact(Timeout = 30000)]
    public async Task Dispose_BeforeStart_IsIdempotent()
    {
        var server = new TcpServer(CreateServerConfig(FreePort()));

        server.Dispose();
        server.Dispose();
        await server.DisposeAsync();

        Assert.Equal(ServerState.Stopped, server.State);
        await Assert.ThrowsAsync<ObjectDisposedException>(() => server.StartAsync());
    }

    private static async Task DisposeAllAsync(IEnumerable<IAsyncDisposable> items)
    {
        foreach (IAsyncDisposable item in items)
            await item.DisposeAsync();
    }
}
