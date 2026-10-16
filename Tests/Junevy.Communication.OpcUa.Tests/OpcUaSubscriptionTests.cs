using Junevy.Communication.Core.Results;
using Junevy.Communication.OpcUa.Tests.TestSupport;
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

        // 用一次真实的写入触发数据变更：服务器侧的 OnWrite 会更新值并通知监控项。
        // 注意：进程内服务器后续通知里的值是空的（F30），因此这里只验证"又来了一条通知"。
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
        using var collector = new CollectingLoggerProvider();
        using ILoggerFactory verbose = LoggerFactory.Create(builder =>
        {
            builder.SetMinimumLevel(LogLevel.Debug);
            builder.AddProvider(collector);
        });

        await using OpcUaTestServer server = await OpcUaTestServerFactory.StartOwnAsync(loggerFactory: verbose);
        await using OpcUaClient client = OpcUaClient.Create(OptionsFor(server.EndpointUrl), verbose);
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

        Assert.Contains(collector.Messages, message => message.Contains("data changed handler threw", StringComparison.Ordinal));
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
        await subscription.DisposeAsync();
        await subscription.DisposeAsync();

        int after = Count(received);
        Assert.True((await client.WriteAsync(server.Id("Test/Counter").ToString(), 1234)).IsSuccess);
        await Task.Delay(500);

        Assert.Equal(after, Count(received));
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

    // 进程内服务器的监控项不一定每次写都发通知（F30），因此反复写直到条件满足。
    private static async Task PumpAsync(OpcUaClient client, OpcUaTestServer server, Func<bool> until)
    {
        DateTime deadline = DateTime.UtcNow.AddSeconds(10);
        int value = 1;
        while (DateTime.UtcNow < deadline && !until())
        {
            Assert.True((await client.WriteAsync(server.Id("Test/Counter").ToString(), value++)).IsSuccess);
            await Task.Delay(50);
        }

        Assert.True(until(), "The server never published the expected notification (F30).");
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

    private sealed class CollectingLoggerProvider : ILoggerProvider
    {
        private readonly List<string> messages = new List<string>();

        public IReadOnlyList<string> Messages
        {
            get
            {
                lock (messages)
                {
                    return messages.ToArray();
                }
            }
        }

        public ILogger CreateLogger(string categoryName) => new CollectingLogger(messages);

        public void Dispose()
        {
        }

        private sealed class CollectingLogger : ILogger
        {
            private readonly List<string> messages;

            public CollectingLogger(List<string> messages) => this.messages = messages;

            public IDisposable? BeginScope<TState>(TState state)
                where TState : notnull => null;

            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(
                LogLevel logLevel,
                EventId eventId,
                TState state,
                Exception? exception,
                Func<TState, Exception?, string> formatter)
            {
                lock (messages)
                {
                    messages.Add(formatter(state, exception));
                }
            }
        }
    }
}
