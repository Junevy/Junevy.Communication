using System.Diagnostics;
using Junevy.Communication.Channels;
using static Junevy.Communication.Tcp.Tests.ServerTestHelpers;
using static Junevy.Communication.Tcp.Tests.TcpTestHelpers;

namespace Junevy.Communication.Tcp.Tests;

/// <summary>
/// 事件处理器内的重入调用不得死锁（计划 D16 边界，审阅者补充的固定规则）。处理器内以同步方式调用停止、释放或关闭，
/// 调用必须在明显短于 <c>StopTimeout</c> 的时间内返回，并且服务端最终进入 Stopped。
/// 每个测试都有上限（<c>Fact(Timeout)</c> 与 <see cref="TcpTestHelpers.WithinAsync{T}(Task{T}, int)"/>），挂起时测试失败而不是卡住。
/// </summary>
[Collection(SocketTimingCollection.Name)]
public sealed class TcpServerReentrancyTests
{
    // 处理器内同步调用的耗时上限：远小于 StopTimeout（3000 毫秒），若调用会等待停止流程，就会超出此上限。
    private const int HandlerLimitMilliseconds = 1000;

    [Fact(Timeout = 30000)]
    public async Task StopFromSessionFrameHandler_NoDeadlock()
    {
        int port = FreePort();
        await using var server = new TcpServer(CreateServerConfig(port));
        var handlerDuration = new TaskCompletionSource<TimeSpan>(TaskCreationOptions.RunContinuationsAsynchronously);
#pragma warning disable xUnit1031 // 被测行为就是处理器内的同步等待：派发上下文内必须立即返回。
        server.FrameReceived += (sender, args) =>
        {
            var stopwatch = Stopwatch.StartNew();
            server.StopAsync().GetAwaiter().GetResult();
            handlerDuration.TrySetResult(stopwatch.Elapsed);
        };
#pragma warning restore xUnit1031
        Assert.True((await WithinAsync(server.StartAsync(), 10000)).IsSuccess);

        await using TcpClientChannel client = CreateClient(port);
        Assert.True((await WithinAsync(client.ConnectAsync(), 10000)).IsSuccess);
        await client.SendAsync(Ascii("TRIGGER"));

        TimeSpan elapsed = await WithinAsync(handlerDuration.Task, 10000);
        Assert.True(elapsed.TotalMilliseconds < HandlerLimitMilliseconds, $"StopAsync blocked the frame handler for {elapsed.TotalMilliseconds:F0} ms.");
        await WaitUntilAsync(() => server.State == ServerState.Stopped, 10000);
        await WaitUntilAsync(() => !client.IsConnected, 5000);
    }

    [Fact(Timeout = 30000)]
    public async Task CloseSessionFromSessionConnectedHandler_NoDeadlock()
    {
        int port = FreePort();
        await using var server = new TcpServer(CreateServerConfig(port));
        var handlerDuration = new TaskCompletionSource<TimeSpan>(TaskCreationOptions.RunContinuationsAsynchronously);
        var closedReason = new TaskCompletionSource<DisconnectReason>(TaskCreationOptions.RunContinuationsAsynchronously);
#pragma warning disable xUnit1031 // 被测行为就是处理器内的同步等待：派发上下文内必须立即返回。
        server.SessionConnected += (sender, args) =>
        {
            var stopwatch = Stopwatch.StartNew();
            args.Session.CloseAsync().GetAwaiter().GetResult();
            handlerDuration.TrySetResult(stopwatch.Elapsed);
        };
#pragma warning restore xUnit1031
        server.SessionClosed += (sender, args) => closedReason.TrySetResult(args.Reason);
        Assert.True((await WithinAsync(server.StartAsync(), 10000)).IsSuccess);

        await using TcpClientChannel client = CreateClient(port);
        Assert.True((await WithinAsync(client.ConnectAsync(), 10000)).IsSuccess);

        TimeSpan elapsed = await WithinAsync(handlerDuration.Task, 10000);
        Assert.True(elapsed.TotalMilliseconds < HandlerLimitMilliseconds, $"CloseAsync blocked the connected handler for {elapsed.TotalMilliseconds:F0} ms.");
        DisconnectReason reason = await WithinAsync(closedReason.Task, 10000);
        Assert.Equal(DisconnectReason.UserRequested, reason);
        await WaitUntilAsync(() => server.SessionCount == 0, 5000);
    }

    [Fact(Timeout = 30000)]
    public async Task DisposeFromSessionClosedHandler_NoDeadlock()
    {
        int port = FreePort();
        var server = new TcpServer(CreateServerConfig(port));   // 由处理器释放，因此这里不使用 await using。
        var handlerDuration = new TaskCompletionSource<TimeSpan>(TaskCreationOptions.RunContinuationsAsynchronously);
        server.SessionClosed += (sender, args) =>
        {
            var stopwatch = Stopwatch.StartNew();
            server.Dispose();   // 同步释放（D16）：派发上下文内只发出停止信号。
            handlerDuration.TrySetResult(stopwatch.Elapsed);
        };
        Assert.True((await WithinAsync(server.StartAsync(), 10000)).IsSuccess);

        await using TcpClientChannel client = CreateClient(port);
        Assert.True((await WithinAsync(client.ConnectAsync(), 10000)).IsSuccess);
        await WithinAsync(client.DisconnectAsync(), 5000);

        TimeSpan elapsed = await WithinAsync(handlerDuration.Task, 10000);
        Assert.True(elapsed.TotalMilliseconds < HandlerLimitMilliseconds, $"Dispose blocked the closed handler for {elapsed.TotalMilliseconds:F0} ms.");
        await WaitUntilAsync(() => server.State == ServerState.Stopped, 10000);
    }
}
