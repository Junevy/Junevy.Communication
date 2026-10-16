using Junevy.Communication.Core.Results;
using Junevy.Communication.OpcUa.Tests.TestSupport;
using Microsoft.Extensions.Logging;
using Xunit;

namespace Junevy.Communication.OpcUa.Tests;

/// <summary>
/// 浏览（P62-D9）：一次拿全正向层级引用、继续点被追完、返回的 NodeId 能直接用来读。
/// </summary>
[Collection(SocketTimingCollection.Name)]
public sealed class OpcUaBrowseTests
{
    private readonly ILoggerFactory loggerFactory;

    public OpcUaBrowseTests()
        => loggerFactory = LoggerFactory.Create(builder => builder.SetMinimumLevel(LogLevel.Warning));

    private OpcUaClientOptions OptionsFor(string endpointUrl) => new OpcUaClientOptions
    {
        EndpointUrl = endpointUrl,
        PkiRoot = OpcUaTestServerFactory.UniquePkiRoot(),
        ConnectTimeout = 10000,
        OperationTimeout = 5000,
    };

    private async Task<OpcUaClient> ConnectedAsync(OpcUaTestServer server)
    {
        OpcUaClient client = OpcUaClient.Create(OptionsFor(server.EndpointUrl), loggerFactory);
        Assert.True((await client.ConnectAsync()).IsSuccess);
        return client;
    }

    [Fact]
    public async Task Browse_ObjectsFolder_SeesTheTestFolder()
    {
        await using OpcUaTestServer server = await OpcUaTestServerFactory.StartOwnAsync(loggerFactory: loggerFactory);
        await using OpcUaClient client = await ConnectedAsync(server);

        CommResult<IReadOnlyList<OpcUaReference>> result = await client.BrowseAsync("i=85");

        Assert.True(result.IsSuccess, result.ToString());
        Assert.Contains(
            result.Data,
            reference => reference.NodeId == server.Id("Test").ToString() && reference.NodeClass == OpcUaNodeClass.Object);
    }

    [Fact]
    public async Task Browse_UnknownNode_FailsWithTheServerCode()
    {
        await using OpcUaTestServer server = await OpcUaTestServerFactory.StartOwnAsync(loggerFactory: loggerFactory);
        await using OpcUaClient client = await ConnectedAsync(server);

        CommResult<IReadOnlyList<OpcUaReference>> result = await client.BrowseAsync("i=999999");

        Assert.False(result.IsSuccess);
        Assert.Equal(0x80340000u, result.ProtocolErrorCode);
    }

    [Theory]
    [InlineData("")]
    [InlineData("i=not-a-number")]
    [InlineData("nsu=urn:unknown:namespace;i=1")]
    public async Task Browse_InvalidNodeId_ReturnsInvalidRequest(string nodeId)
    {
        await using OpcUaTestServer server = await OpcUaTestServerFactory.StartOwnAsync(loggerFactory: loggerFactory);
        await using OpcUaClient client = await ConnectedAsync(server);

        CommResult<IReadOnlyList<OpcUaReference>> result = await client.BrowseAsync(nodeId);

        Assert.False(result.IsSuccess);
        Assert.Equal(CommErrorKind.InvalidRequest, result.ErrorKind);
    }

    [Fact]
    public async Task Browse_WithoutAConnection_ReportsNotConnected()
    {
        await using OpcUaClient client = OpcUaClient.Create(
            OptionsFor(OpcUaTestServerFactory.FreeEndpointUrl()),
            loggerFactory);

        CommResult<IReadOnlyList<OpcUaReference>> result = await client.BrowseAsync("i=85");

        Assert.False(result.IsSuccess);
        Assert.Equal(CommErrorKind.NotConnected, result.ErrorKind);
    }
}
