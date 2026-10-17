using Junevy.Communication.Core.Results;
using Junevy.Communication.OpcUa.Tests.TestSupport;
using Microsoft.Extensions.Logging;
using Xunit;

namespace Junevy.Communication.OpcUa.Tests;

/// <summary>
/// 释放的规则（计划第 7 节第 2 条、P6-C8）。P6.1 验收发现的三个缺陷在这里都要有用例：
/// 释放时正在进行的连接返回失败而不是抛异常；释放后的每个操作返回 <c>ConnectionClosed</c>；
/// 释放不释放仍可能被使用的同步原语。
/// </summary>
[Collection(SocketTimingCollection.Name)]
public sealed class OpcUaDisposeTests
{
    private readonly ILoggerFactory loggerFactory;

    public OpcUaDisposeTests()
        => loggerFactory = LoggerFactory.Create(builder => builder.SetMinimumLevel(LogLevel.Warning));

    private OpcUaClientOptions OptionsFor(string endpointUrl) => new OpcUaClientOptions
    {
        EndpointUrl = endpointUrl,
        PkiRoot = OpcUaTestServerFactory.UniquePkiRoot(),
        ConnectTimeout = 20000,
        OperationTimeout = 5000,
        ReconnectInterval = 200,
        MaxReconnectInterval = 1000,
    };

    [Fact]
    public async Task Dispose_WhileConnecting_ReturnsAFailedResultInsteadOfThrowing()
    {
        using var silent = new SilentOpcUaPort();
        OpcUaClientOptions options = OptionsFor(silent.EndpointUrl);
        options.ConnectTimeout = 20000;
        await using OpcUaClient client = OpcUaClient.Create(options, loggerFactory);

        Task<CommResult> connecting = client.ConnectAsync();
        DateTime disposedAt = DateTime.UtcNow;
        await client.DisposeAsync();
        CommResult result = await connecting;
        TimeSpan elapsed = DateTime.UtcNow - disposedAt;

        Assert.False(result.IsSuccess);
        Assert.Equal(OpcUaSessionState.Disconnected, client.State);

        // 释放要主动取消进行中的连接：ConnectTimeout 是 20 秒，等它自然超时就等于释放没起作用。
        Assert.InRange(elapsed.TotalMilliseconds, 0, 5000);
    }

    [Fact]
    public async Task Operations_AfterDispose_ReportConnectionClosed()
    {
        await using OpcUaTestServer server = await OpcUaTestServerFactory.StartOwnAsync(loggerFactory: loggerFactory);
        OpcUaClient client = OpcUaClient.Create(OptionsFor(server.EndpointUrl), loggerFactory);
        Assert.True((await client.ConnectAsync()).IsSuccess);

        await client.DisposeAsync();

        string nodeId = server.Id("Test/Int32").ToString()!;
        Assert.Equal(CommErrorKind.ConnectionClosed, (await client.DisconnectAsync()).ErrorKind);
        Assert.Equal(CommErrorKind.ConnectionClosed, (await client.ReadAsync(new[] { nodeId })).ErrorKind);
        Assert.Equal(
            CommErrorKind.ConnectionClosed,
            (await client.WriteAsync(new[] { new OpcUaWriteValue(nodeId, 1) })).ErrorKind);
        Assert.Equal(CommErrorKind.ConnectionClosed, (await client.BrowseAsync(nodeId)).ErrorKind);
        Assert.Equal(
            CommErrorKind.ConnectionClosed,
            (await client.SubscribeAsync(new[] { nodeId }, (_, _) => { })).ErrorKind);
        await Assert.ThrowsAsync<ObjectDisposedException>(() => client.ConnectAsync());
    }

    [Fact]
    public async Task Dispose_CanBeCalledRepeatedly_AndClosesTheSessionOnTheServer()
    {
        await using OpcUaTestServer server = await OpcUaTestServerFactory.StartOwnAsync(loggerFactory: loggerFactory);
        OpcUaClient client = OpcUaClient.Create(OptionsFor(server.EndpointUrl), loggerFactory);
        Assert.True((await client.ConnectAsync()).IsSuccess);
        Assert.Equal(1, server.SessionCount);

        await client.DisposeAsync();
        await client.DisposeAsync();
        client.Dispose();

        Assert.Equal(OpcUaSessionState.Disconnected, client.State);
        Assert.Equal(0, server.SessionCount);
    }

    [Fact]
    public async Task Disconnect_ThenConnectAgain_OpensAFreshSession()
    {
        await using OpcUaTestServer server = await OpcUaTestServerFactory.StartOwnAsync(loggerFactory: loggerFactory);
        await using OpcUaClient client = OpcUaClient.Create(OptionsFor(server.EndpointUrl), loggerFactory);

        Assert.True((await client.ConnectAsync()).IsSuccess);
        Assert.True((await client.DisconnectAsync()).IsSuccess);
        Assert.True((await client.ConnectAsync()).IsSuccess);
        Assert.Equal(OpcUaSessionState.Connected, client.State);
        Assert.Equal(1, server.SessionCount);

        Assert.True((await client.DisconnectAsync()).IsSuccess);
        Assert.Equal(0, server.SessionCount);
    }

    [Fact]
    public async Task Operations_RacingADisconnect_ReturnResultsInsteadOfThrowing()
    {
        await using OpcUaTestServer server = await OpcUaTestServerFactory.StartOwnAsync(loggerFactory: loggerFactory);
        await using OpcUaClient client = OpcUaClient.Create(OptionsFor(server.EndpointUrl), loggerFactory);
        string[] nodes = { server.Id("Test/Int32").ToString()! };

        for (int round = 0; round < 5; round++)
        {
            Assert.True((await client.ConnectAsync()).IsSuccess);

            // 断开与进行中的读同时发生：读要么成功，要么给出连接类的失败结果，不能把异常抛给调用方（P6-C8）。
            Task<CommResult<IReadOnlyList<OpcUaValue>>>[] reads = Enumerable.Range(0, 20)
                .Select(_ => Task.Run(() => client.ReadAsync(nodes)))
                .ToArray();
            await client.DisconnectAsync();

            foreach (CommResult<IReadOnlyList<OpcUaValue>> read in await Task.WhenAll(reads))
            {
                Assert.True(
                    read.IsSuccess || read.ErrorKind is CommErrorKind.NotConnected or CommErrorKind.ConnectionClosed,
                    read.ToString());
            }
        }
    }

    [Fact]
    public async Task Dispose_DoesNotBreakOtherClientsSharingTheServer()
    {
        await using OpcUaTestServer server = await OpcUaTestServerFactory.StartOwnAsync(loggerFactory: loggerFactory);
        await using OpcUaClient first = OpcUaClient.Create(OptionsFor(server.EndpointUrl), loggerFactory);
        OpcUaClient second = OpcUaClient.Create(OptionsFor(server.EndpointUrl), loggerFactory);

        Assert.True((await first.ConnectAsync()).IsSuccess);
        Assert.True((await second.ConnectAsync()).IsSuccess);

        await first.DisposeAsync();

        Assert.Equal(OpcUaSessionState.Connected, second.State);
        Assert.Equal(1, server.SessionCount);
        Assert.True((await second.DisconnectAsync()).IsSuccess);
        await second.DisposeAsync();
    }

    [Fact]
    public async Task Dispose_WithNothingConnected_LeavesNoBackgroundWork()
    {
        await using OpcUaTestServer server = await OpcUaTestServerFactory.StartOwnAsync(loggerFactory: loggerFactory);
        OpcUaClient client = OpcUaClient.Create(OptionsFor(server.EndpointUrl), loggerFactory);

        await client.DisposeAsync();

        Assert.Equal(OpcUaSessionState.Disconnected, client.State);
    }
}
