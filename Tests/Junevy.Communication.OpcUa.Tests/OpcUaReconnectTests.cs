using Junevy.Communication.Core.Results;
using Junevy.Communication.OpcUa.Tests.TestSupport;
using Microsoft.Extensions.Logging;
using Xunit;

namespace Junevy.Communication.OpcUa.Tests;

/// <summary>
/// 重连（P62-D2、D11）：服务器停掉后的状态与"四个操作立刻 <c>NotConnected</c>"、同一端口重启后的恢复、
/// 重建后的订阅继续收通知、重连期间断开与释放、强制 GC 后没有未观察的任务异常（Q40）。
/// 都要停、启自己的服务器实例，因此不放进共用夹具；需要真实套接字，放进 <c>SocketTiming</c> 集合。
/// </summary>
[Collection(SocketTimingCollection.Name)]
public sealed class OpcUaReconnectTests
{
    private const int StateTimeoutMilliseconds = 30000;

    private readonly ILoggerFactory loggerFactory;

    public OpcUaReconnectTests()
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
    public async Task ServerStops_StateBecomesReconnecting_AndEveryOperationReportsNotConnected()
    {
        await using OpcUaTestServer server = await OpcUaTestServerFactory.StartOwnAsync(loggerFactory: loggerFactory);
        await using OpcUaClient client = OpcUaClient.Create(OptionsFor(server.EndpointUrl), loggerFactory);

        var nodeId = server.Id("Test/Counter").ToString()!;
        var seen = new List<OpcUaSessionState>();
        client.StateChanged += (_, args) => seen.Add(args.Current);

        Assert.True((await client.ConnectAsync()).IsSuccess);
        await server.StopHostAsync();

        await WaitForStateAsync(client, OpcUaSessionState.Reconnecting);
        Assert.Contains(OpcUaSessionState.Reconnecting, seen);

        // P62-D11：不是 Connected 就不让调用方排队等，直接给 NotConnected。
        Assert.Equal(CommErrorKind.NotConnected, (await client.ReadAsync(new[] { nodeId })).ErrorKind);
        Assert.Equal(
            CommErrorKind.NotConnected,
            (await client.WriteAsync(new[] { new OpcUaWriteValue(nodeId, 1) })).ErrorKind);
        Assert.Equal(CommErrorKind.NotConnected, (await client.BrowseAsync(nodeId)).ErrorKind);
        Assert.Equal(
            CommErrorKind.NotConnected,
            (await client.SubscribeAsync(new[] { nodeId }, (_, _) => { })).ErrorKind);
    }

    [Fact]
    public async Task ServerRestartsOnTheSamePort_TheClientComesBackToConnected()
    {
        await using OpcUaTestServer server = await OpcUaTestServerFactory.StartOwnAsync(loggerFactory: loggerFactory);
        await using OpcUaClient client = OpcUaClient.Create(OptionsFor(server.EndpointUrl), loggerFactory);

        Assert.True((await client.ConnectAsync()).IsSuccess);
        await server.StopHostAsync();
        await WaitForStateAsync(client, OpcUaSessionState.Reconnecting);

        await server.StartHostAsync();

        await WaitForStateAsync(client, OpcUaSessionState.Connected);
        Assert.True((await client.ReadAsync(new[] { server.Id("Test/Int32").ToString()! })).IsSuccess);
    }

    [Fact]
    public async Task SubscriptionCreatedBeforeTheStopKeepsNotifyingAfterTheRestart()
    {
        await using OpcUaTestServer server = await OpcUaTestServerFactory.StartOwnAsync(loggerFactory: loggerFactory);
        await using OpcUaClient client = OpcUaClient.Create(OptionsFor(server.EndpointUrl), loggerFactory);
        Assert.True((await client.ConnectAsync()).IsSuccess);

        var nodeId = server.Id("Test/Counter").ToString()!;
        (OpcUaSubscription subscribed, List<OpcUaDataChangedEventArgs> received) = await KeepAsync(client, nodeId);
        await using OpcUaSubscription subscription = subscribed;

        await WaitUntilAsync(() => Count(received) >= 1, "the initial value");

        await server.StopHostAsync();
        await WaitForStateAsync(client, OpcUaSessionState.Reconnecting);
        await server.StartHostAsync();

        // 服务器重启后监控项被重建（WithTransferSubscriptionsOnRecreate），处理器继续收到通知。
        await WaitUntilRecoveredAsync(client, nodeId, received);
        Assert.Equal(nodeId, received[received.Count - 1].NodeId);
    }

    [Fact]
    public async Task AfterADisconnectAndAFreshConnect_ALaterDropStillRecovers()
    {
        // P6.1 验收发现的第一类缺陷："停止"与"释放"不能混为一谈。断开只结束那一次会话，
        // 之后再连上，下一次意外断线照样要自动恢复。
        await using OpcUaTestServer server = await OpcUaTestServerFactory.StartOwnAsync(loggerFactory: loggerFactory);
        await using OpcUaClient client = OpcUaClient.Create(OptionsFor(server.EndpointUrl), loggerFactory);

        Assert.True((await client.ConnectAsync()).IsSuccess);
        await client.DisconnectAsync();
        Assert.True((await client.ConnectAsync()).IsSuccess);

        await server.StopHostAsync();
        await WaitForStateAsync(client, OpcUaSessionState.Reconnecting);
        await server.StartHostAsync();

        await WaitForStateAsync(client, OpcUaSessionState.Connected);
        Assert.True((await client.ReadAsync(new[] { server.Id("Test/Int32").ToString()! })).IsSuccess);
        Assert.Equal(1, server.SessionCount);
    }

    [Fact]
    public async Task DisposedOrFailedSubscriptions_AreNotRecreatedWhenTheSessionRecovers()
    {
        await using OpcUaTestServer server = await OpcUaTestServerFactory.StartOwnAsync(loggerFactory: loggerFactory);
        await using OpcUaClient client = OpcUaClient.Create(OptionsFor(server.EndpointUrl), loggerFactory);
        Assert.True((await client.ConnectAsync()).IsSuccess);

        var nodeId = server.Id("Test/Counter").ToString()!;
        (OpcUaSubscription live, List<OpcUaDataChangedEventArgs> kept) = await KeepAsync(client, nodeId);
        await using OpcUaSubscription keep = live;

        // 一个建好之后释放的订阅，一个被调用方取消、没有建成的订阅。
        CommResult<OpcUaSubscription> dropped = await client.SubscribeAsync(new[] { nodeId }, (_, _) => { }, FastOptions());
        Assert.True(dropped.IsSuccess, dropped.ToString());
        await dropped.Data.DisposeAsync();

        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        CommResult<OpcUaSubscription> failed = await client.SubscribeAsync(
            new[] { nodeId },
            (_, _) => { },
            FastOptions(),
            cancelled.Token);
        Assert.Equal(CommErrorKind.Cancelled, failed.ErrorKind);
        Assert.Equal(1, server.SubscriptionCount);

        await server.StopHostAsync();
        await WaitForStateAsync(client, OpcUaSessionState.Reconnecting);
        await server.StartHostAsync();

        // 会话恢复时 SDK 会重建它名下的全部订阅：已经释放或没建成的订阅如果还挂在会话上，就会在新服务器上复活。
        await WaitUntilRecoveredAsync(client, nodeId, kept);
        Assert.Equal(1, server.SubscriptionCount);
    }

    [Fact]
    public async Task DisposeASubscriptionWhileReconnecting_ReturnsPromptlyAndItStaysGone()
    {
        await using OpcUaTestServer server = await OpcUaTestServerFactory.StartOwnAsync(loggerFactory: loggerFactory);
        await using OpcUaClient client = OpcUaClient.Create(OptionsFor(server.EndpointUrl), loggerFactory);
        Assert.True((await client.ConnectAsync()).IsSuccess);

        var nodeId = server.Id("Test/Counter").ToString()!;
        (OpcUaSubscription live, List<OpcUaDataChangedEventArgs> kept) = await KeepAsync(client, nodeId);
        await using OpcUaSubscription keep = live;

        CommResult<OpcUaSubscription> dropped = await client.SubscribeAsync(new[] { nodeId }, (_, _) => { }, FastOptions());
        Assert.True(dropped.IsSuccess, dropped.ToString());

        await server.StopHostAsync();
        await WaitForStateAsync(client, OpcUaSessionState.Reconnecting);

        // 重连期间服务器不可达：释放立刻返回，不挂在 SDK 里等（重连不限次数），摘除在恢复之后由后台完成。
        DateTime started = DateTime.UtcNow;
        await dropped.Data.DisposeAsync();
        Assert.InRange((DateTime.UtcNow - started).TotalMilliseconds, 0, 2000);

        // 之后的每一次重连都不能被这个订阅拖垮（F32），恢复之后它也不能复活。
        await server.StartHostAsync();
        await WaitUntilRecoveredAsync(client, nodeId, kept);
        Assert.Equal(1, server.SubscriptionCount);
    }

    [Fact]
    public async Task DisconnectWhileReconnecting_StopsTheReconnectLoop()
    {
        await using OpcUaTestServer server = await OpcUaTestServerFactory.StartOwnAsync(loggerFactory: loggerFactory);
        await using OpcUaClient client = OpcUaClient.Create(OptionsFor(server.EndpointUrl), loggerFactory);

        Assert.True((await client.ConnectAsync()).IsSuccess);
        await server.StopHostAsync();
        await WaitForStateAsync(client, OpcUaSessionState.Reconnecting);

        await client.DisconnectAsync();
        Assert.Equal(OpcUaSessionState.Disconnected, client.State);

        // 服务器回来之后也不该自己连上去。
        await server.StartHostAsync();
        await Task.Delay(2000);
        Assert.Equal(OpcUaSessionState.Disconnected, client.State);
    }

    [Fact]
    public async Task DisposeWhileReconnecting_ReturnsPromptlyAndLeavesTheClientDisconnected()
    {
        await using OpcUaTestServer server = await OpcUaTestServerFactory.StartOwnAsync(loggerFactory: loggerFactory);
        OpcUaClient client = OpcUaClient.Create(OptionsFor(server.EndpointUrl), loggerFactory);

        Assert.True((await client.ConnectAsync()).IsSuccess);
        await server.StopHostAsync();
        await WaitForStateAsync(client, OpcUaSessionState.Reconnecting);

        DateTime disposedAt = DateTime.UtcNow;
        await client.DisposeAsync();
        TimeSpan elapsed = DateTime.UtcNow - disposedAt;

        // 释放不能干等重连退避（P6-C8），也不能把状态留在 Reconnecting。
        Assert.InRange(elapsed.TotalMilliseconds, 0, 5000);
        Assert.Equal(OpcUaSessionState.Disconnected, client.State);

        await server.StartHostAsync();
        await Task.Delay(2000);
        Assert.Equal(OpcUaSessionState.Disconnected, client.State);
    }

    [Fact]
    public async Task ForcedCollection_AfterTheClientIsGone_FindsNoUnobservedTaskException()
    {
        var unobserved = new List<Exception>();

        void OnUnobserved(object? sender, UnobservedTaskExceptionEventArgs args)
        {
            lock (unobserved)
            {
                unobserved.Add(args.Exception);
            }

            args.SetObserved();
        }

        TaskScheduler.UnobservedTaskException += OnUnobserved;
        try
        {
            await ReconnectAndDropAsync();

            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
            await Task.Delay(1000);

            lock (unobserved)
            {
                Assert.Empty(unobserved);
            }
        }
        finally
        {
            TaskScheduler.UnobservedTaskException -= OnUnobserved;
        }
    }

    // 客户端、订阅与服务器都在这一层的栈上走完，返回后全部不可达，GC 才真的能收走。
    private async Task ReconnectAndDropAsync()
    {
        await using OpcUaTestServer server = await OpcUaTestServerFactory.StartOwnAsync(loggerFactory: loggerFactory);
        OpcUaClient client = OpcUaClient.Create(OptionsFor(server.EndpointUrl), loggerFactory);
        try
        {
            Assert.True((await client.ConnectAsync()).IsSuccess);

            CommResult<OpcUaSubscription> subscribed = await client.SubscribeAsync(
                new[] { server.Id("Test/Counter").ToString()! },
                (_, _) => { },
                FastOptions());
            Assert.True(subscribed.IsSuccess, subscribed.ToString());
            await subscribed.Data.DisposeAsync();

            await server.StopHostAsync();
            await WaitForStateAsync(client, OpcUaSessionState.Reconnecting);
        }
        finally
        {
            await client.DisposeAsync();
        }
    }

    private static OpcUaSubscriptionOptions FastOptions() => new OpcUaSubscriptionOptions
    {
        PublishingInterval = 100,
        SamplingInterval = 50,
        QueueSize = 10,
        DiscardOldest = false,
    };

    // 一个一直保留的订阅：会话恢复之后它又收到通知，就说明 SDK 已经转移或重建完订阅。
    private static async Task<(OpcUaSubscription Subscription, List<OpcUaDataChangedEventArgs> Received)> KeepAsync(
        OpcUaClient client,
        string nodeId)
    {
        var received = new List<OpcUaDataChangedEventArgs>();
        CommResult<OpcUaSubscription> subscribed = await client.SubscribeAsync(
            new[] { nodeId },
            (_, args) => { lock (received) { received.Add(args); } },
            FastOptions());
        Assert.True(subscribed.IsSuccess, subscribed.ToString());
        return (subscribed.Data, received);
    }

    // 回到 Connected 之后，再等保留的订阅收到一条新通知（订阅已经恢复），并给后台的摘除留一点时间。
    private static async Task WaitUntilRecoveredAsync(OpcUaClient client, string nodeId, List<OpcUaDataChangedEventArgs> kept)
    {
        await WaitForStateAsync(client, OpcUaSessionState.Connected);
        int afterRestart = Count(kept);
        await PumpUntilAsync(client, nodeId, () => Count(kept) > afterRestart);
        await Task.Delay(1000);
    }

    // 进程内服务器的监控项不一定每次写都发通知（F29），因此反复写直到条件满足。
    private static async Task PumpUntilAsync(OpcUaClient client, string nodeId, Func<bool> until)
    {
        DateTime deadline = DateTime.UtcNow.AddSeconds(20);
        int value = 1;
        while (DateTime.UtcNow < deadline && !until())
        {
            Assert.True((await client.WriteAsync(nodeId, value++)).IsSuccess);
            await Task.Delay(50);
        }

        Assert.True(until(), "The server never published a notification after the restart (F29).");
    }

    private static async Task WaitForStateAsync(OpcUaClient client, OpcUaSessionState expected)
    {
        DateTime deadline = DateTime.UtcNow.AddMilliseconds(StateTimeoutMilliseconds);
        while (DateTime.UtcNow < deadline)
        {
            if (client.State == expected)
            {
                return;
            }

            await Task.Delay(25);
        }

        Assert.Fail($"The client did not reach {expected}; it stayed at {client.State}.");
    }

    private static async Task WaitUntilAsync(Func<bool> condition, string what)
    {
        DateTime deadline = DateTime.UtcNow.AddMilliseconds(StateTimeoutMilliseconds);
        while (DateTime.UtcNow < deadline)
        {
            if (condition())
            {
                return;
            }

            await Task.Delay(25);
        }

        Assert.Fail($"The test timed out waiting for {what}.");
    }

    private static int Count(List<OpcUaDataChangedEventArgs> received)
    {
        lock (received)
        {
            return received.Count;
        }
    }
}