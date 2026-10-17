using Junevy.Communication.Core.Results;
using Junevy.Communication.OpcUa.Tests.TestSupport;
using Junevy.Communication.Testing;
using Microsoft.Extensions.Logging;
using Xunit;

namespace Junevy.Communication.OpcUa.Tests;

/// <summary>
/// 订阅（P62-D10）：初始值、后续通知、逐项状态、处理器异常、释放与重复释放。
/// </summary>
[Collection(SocketTimingCollection.Name)]
public sealed class OpcUaSubscriptionTests
{
    private readonly ILoggerFactory loggerFactory;

    public OpcUaSubscriptionTests()
        => loggerFactory = LoggerFactory.Create(builder => builder.SetMinimumLevel(LogLevel.Warning));

    private OpcUaClientOptions OptionsFor(string endpointUrl) => new OpcUaClientOptions
    {
        EndpointUrl = endpointUrl,
        PkiRoot = OpcUaTestServerFactory.UniquePkiRoot(),
        ConnectTimeout = 10000,
        OperationTimeout = 5000,
    };

    private static OpcUaSubscriptionOptions FastOptions() => new OpcUaSubscriptionOptions
    {
        PublishingInterval = 100,
        SamplingInterval = 50,
        QueueSize = 10,
        DiscardOldest = false,
    };

    private async Task<OpcUaClient> ConnectedAsync(OpcUaTestServer server)
    {
        OpcUaClient client = OpcUaClient.Create(OptionsFor(server.EndpointUrl), loggerFactory);
        Assert.True((await client.ConnectAsync()).IsSuccess);
        return client;
    }

    [Fact]
    public async Task Subscribe_DeliversTheInitialValueAndLaterChanges()
    {
        await using OpcUaTestServer server = await OpcUaTestServerFactory.StartOwnAsync(loggerFactory: loggerFactory);
        await using OpcUaClient client = await ConnectedAsync(server);

        var received = new List<OpcUaDataChangedEventArgs>();
        CommResult<OpcUaSubscription> result = await client.SubscribeAsync(
            new[] { server.Id("Test/Counter").ToString() },
            (_, args) => { lock (received) { received.Add(args); } },
            FastOptions());

        Assert.True(result.IsSuccess, result.ToString());
        await using OpcUaSubscription subscription = result.Data;
        Assert.Single(subscription.NodeIds);
        Assert.True(subscription.ItemStatuses[0].IsGood);

        await WaitUntilAsync(() => Count(received) >= 1, "the initial value");
        Assert.Equal(server.Id("Test/Counter").ToString(), received[0].NodeId);

        // 通知里带的是真的数据值，不是一个空的 DataValue（F27：值在 MonitoredItemNotification.Value 上）。
        Assert.Equal(0, received[0].Value.Value);
        Assert.True(received[0].Value.Status.IsGood, received[0].Value.Status.ToString());
        Assert.Equal(DateTimeKind.Utc, received[0].Value.SourceTimestamp?.Kind);

        // 用一次真实的写入触发数据变更：服务器侧的 OnWrite 会更新值并通知监控项。
        // 注意：进程内服务器后续通知里的值是空的（F29），因此这里只验证"又来了一条通知"。
        await PumpAsync(client, server, () => Count(received) >= 2);

        OpcUaDataChangedEventArgs changed = received[received.Count - 1];
        Assert.Equal(server.Id("Test/Counter").ToString(), changed.NodeId);
    }

    [Fact]
    public async Task Subscribe_OneUnknownNodeInTheBatch_ThatItemIsBadAndTheOthersStillNotify()
    {
        await using OpcUaTestServer server = await OpcUaTestServerFactory.StartOwnAsync(loggerFactory: loggerFactory);
        await using OpcUaClient client = await ConnectedAsync(server);

        var received = new List<OpcUaDataChangedEventArgs>();
        CommResult<OpcUaSubscription> result = await client.SubscribeAsync(
            new[] { server.Id("Test/Counter").ToString(), "i=999999" },
            (_, args) => { lock (received) { received.Add(args); } },
            FastOptions());

        Assert.True(result.IsSuccess, result.ToString());
        await using OpcUaSubscription subscription = result.Data;
        Assert.Equal(2, subscription.ItemStatuses.Count);
        Assert.True(subscription.ItemStatuses[0].IsGood);
        Assert.True(subscription.ItemStatuses[1].IsBad);

        await WaitUntilAsync(() => Count(received) >= 1, "the initial value of the good item");
        Assert.Equal(server.Id("Test/Counter").ToString(), received[0].NodeId);
    }

    [Fact]
    public async Task Subscribe_HandlerThrows_IsLoggedAndLaterNotificationsStillArrive()
    {
        var logs = new TestLogger();

        await using OpcUaTestServer server = await OpcUaTestServerFactory.StartOwnAsync(loggerFactory: loggerFactory);
        await using OpcUaClient client = OpcUaClient.Create(OptionsFor(server.EndpointUrl), logs);
        Assert.True((await client.ConnectAsync()).IsSuccess);

        var seen = 0;
        CommResult<OpcUaSubscription> result = await client.SubscribeAsync(
            new[] { server.Id("Test/Counter").ToString() },
            (_, _) =>
            {
                if (Interlocked.Increment(ref seen) == 1)
                {
                    throw new InvalidOperationException("handler blew up");
                }
            },
            FastOptions());

        Assert.True(result.IsSuccess, result.ToString());
        await using OpcUaSubscription subscription = result.Data;

        await PumpAsync(client, server, () => Volatile.Read(ref seen) >= 2);

        Assert.Contains(
            logs.GetEntries(LogLevel.Error),
            entry => entry.Message.Contains("OpcUaClient.SubscribeAsync handler", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Subscribe_Dispose_StopsNotificationsAndCanBeCalledTwice()
    {
        await using OpcUaTestServer server = await OpcUaTestServerFactory.StartOwnAsync(loggerFactory: loggerFactory);
        await using OpcUaClient client = await ConnectedAsync(server);

        var received = new List<OpcUaDataChangedEventArgs>();
        CommResult<OpcUaSubscription> result = await client.SubscribeAsync(
            new[] { server.Id("Test/Counter").ToString() },
            (_, args) => { lock (received) { received.Add(args); } },
            FastOptions());

        Assert.True(result.IsSuccess, result.ToString());
        OpcUaSubscription subscription = result.Data;

        await WaitUntilAsync(() => Count(received) >= 1, "the initial value");
        Assert.Equal(1, server.SubscriptionCount);

        await subscription.DisposeAsync();
        await subscription.DisposeAsync();
        Assert.Equal(0, server.SubscriptionCount);

        int after = Count(received);
        Assert.True((await client.WriteAsync(server.Id("Test/Counter").ToString(), 1234)).IsSuccess);
        await Task.Delay(500);

        Assert.Equal(after, Count(received));
    }

    [Fact]
    public async Task Subscribe_Dispose_StillDeletesTheSubscriptionAfterTheSubscribeTokenIsCancelled()
    {
        await using OpcUaTestServer server = await OpcUaTestServerFactory.StartOwnAsync(loggerFactory: loggerFactory);
        await using OpcUaClient client = await ConnectedAsync(server);

        // 调用方的令牌只管"建订阅"这一次调用；之后它被取消（常见于带超时的令牌或应用关闭），
        // 释放订阅仍然要把服务器上的订阅删掉。
        using var subscribing = new CancellationTokenSource();
        CommResult<OpcUaSubscription> result = await client.SubscribeAsync(
            new[] { server.Id("Test/Counter").ToString() },
            (_, _) => { },
            FastOptions(),
            subscribing.Token);
        Assert.True(result.IsSuccess, result.ToString());
        Assert.Equal(1, server.SubscriptionCount);

        subscribing.Cancel();
        await result.Data.DisposeAsync();

        Assert.Equal(0, server.SubscriptionCount);
    }

    [Fact]
    public async Task Subscribe_OneSubscriberOfAMulticastHandlerThrows_TheOthersStillReceiveFromTheClient()
    {
        await using OpcUaTestServer server = await OpcUaTestServerFactory.StartOwnAsync(loggerFactory: loggerFactory);
        await using OpcUaClient client = await ConnectedAsync(server);

        object? sender = null;
        var delivered = 0;
        EventHandler<OpcUaDataChangedEventArgs> handler = (_, _) => throw new InvalidOperationException("first subscriber");
        handler += (source, _) =>
        {
            Volatile.Write(ref sender, source);
            Interlocked.Increment(ref delivered);
        };

        CommResult<OpcUaSubscription> result = await client.SubscribeAsync(
            new[] { server.Id("Test/Counter").ToString() },
            handler,
            FastOptions());
        Assert.True(result.IsSuccess, result.ToString());
        await using OpcUaSubscription subscription = result.Data;

        // 处理器按事件的约定逐个调用（EventSubscribers.InvokeEach）：前一个抛异常不能挡住后一个；sender 是客户端。
        await WaitUntilAsync(() => Volatile.Read(ref delivered) >= 1, "the second subscriber");
        Assert.Same(client, Volatile.Read(ref sender));
    }

    [Fact]
    public async Task Subscribe_InvalidOptionsOrNullHandler_ThrowLikeCreate()
    {
        // 参数校验先于连接状态：不需要服务器。
        await using OpcUaClient client = OpcUaClient.Create(
            OptionsFor(OpcUaTestServerFactory.FreeEndpointUrl()),
            loggerFactory);
        string[] nodes = { "i=2258" };

        await Assert.ThrowsAsync<ArgumentException>(
            "options.PublishingInterval",
            () => client.SubscribeAsync(nodes, (_, _) => { }, new OpcUaSubscriptionOptions { PublishingInterval = 0 }));
        await Assert.ThrowsAsync<ArgumentException>(
            "options.SamplingInterval",
            () => client.SubscribeAsync(nodes, (_, _) => { }, new OpcUaSubscriptionOptions { SamplingInterval = -1 }));
        await Assert.ThrowsAsync<ArgumentException>(
            "options.QueueSize",
            () => client.SubscribeAsync(nodes, (_, _) => { }, new OpcUaSubscriptionOptions { QueueSize = 0 }));
        await Assert.ThrowsAsync<ArgumentNullException>(
            "handler",
            () => client.SubscribeAsync(nodes, null!));
    }

    [Fact]
    public async Task Subscribe_CallerCancels_ReportsCancelledAndLeavesNothingOnTheServer()
    {
        await using OpcUaTestServer server = await OpcUaTestServerFactory.StartOwnAsync(loggerFactory: loggerFactory);
        await using OpcUaClient client = await ConnectedAsync(server);

        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        CommResult<OpcUaSubscription> result = await client.SubscribeAsync(
            new[] { server.Id("Test/Counter").ToString() },
            (_, _) => { },
            FastOptions(),
            cancelled.Token);

        Assert.Equal(CommErrorKind.Cancelled, result.ErrorKind);
        Assert.Equal(0, server.SubscriptionCount);
    }

    [Fact]
    public async Task Subscribe_AfterDisconnect_DoesNotThrow()
    {
        await using OpcUaTestServer server = await OpcUaTestServerFactory.StartOwnAsync(loggerFactory: loggerFactory);
        await using OpcUaClient client = await ConnectedAsync(server);

        CommResult<OpcUaSubscription> result = await client.SubscribeAsync(
            new[] { server.Id("Test/Counter").ToString() },
            (_, _) => { },
            FastOptions());
        Assert.True(result.IsSuccess, result.ToString());

        Assert.True((await client.DisconnectAsync()).IsSuccess);
        await result.Data.DisposeAsync();
    }

    [Fact]
    public async Task Subscribe_EmptyOrDuplicateNodeIds_FailTheCall()
    {
        await using OpcUaTestServer server = await OpcUaTestServerFactory.StartOwnAsync(loggerFactory: loggerFactory);
        await using OpcUaClient client = await ConnectedAsync(server);

        CommResult<OpcUaSubscription> empty = await client.SubscribeAsync(
            Array.Empty<string>(),
            (_, _) => { },
            FastOptions());
        Assert.False(empty.IsSuccess);
        Assert.Equal(CommErrorKind.InvalidRequest, empty.ErrorKind);

        CommResult<OpcUaSubscription> duplicated = await client.SubscribeAsync(
            new[] { server.Id("Test/Counter").ToString(), server.Id("Test/Counter").ToString() },
            (_, _) => { },
            FastOptions());
        Assert.False(duplicated.IsSuccess);
        Assert.Equal(CommErrorKind.InvalidRequest, duplicated.ErrorKind);
    }

    [Fact]
    public async Task Subscribe_WithoutAConnection_ReportsNotConnected()
    {
        await using OpcUaClient client = OpcUaClient.Create(
            OptionsFor(OpcUaTestServerFactory.FreeEndpointUrl()),
            loggerFactory);

        CommResult<OpcUaSubscription> result = await client.SubscribeAsync(
            new[] { "i=2258" },
            (_, _) => { },
            FastOptions());

        Assert.False(result.IsSuccess);
        Assert.Equal(CommErrorKind.NotConnected, result.ErrorKind);
    }

    // 进程内服务器的监控项不一定每次写都发通知（F29），因此反复写直到条件满足。
    private static async Task PumpAsync(OpcUaClient client, OpcUaTestServer server, Func<bool> until)
    {
        DateTime deadline = DateTime.UtcNow.AddSeconds(10);
        int value = 1;
        while (DateTime.UtcNow < deadline && !until())
        {
            Assert.True((await client.WriteAsync(server.Id("Test/Counter").ToString(), value++)).IsSuccess);
            await Task.Delay(50);
        }

        Assert.True(until(), "The server never published the expected notification (F29).");
    }

    private static int Count(List<OpcUaDataChangedEventArgs> received)
    {
        lock (received)
        {
            return received.Count;
        }
    }

    private static async Task WaitUntilAsync(Func<bool> condition, string what, int timeoutMilliseconds = 10000)
    {
        DateTime deadline = DateTime.UtcNow.AddMilliseconds(timeoutMilliseconds);
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
}
