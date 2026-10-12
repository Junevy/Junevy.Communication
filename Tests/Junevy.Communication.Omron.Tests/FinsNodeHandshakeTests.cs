using System.Collections.Concurrent;
using Junevy.Communication.Channels;
using Junevy.Communication.Core.Results;
using Junevy.Communication.Omron.Framing;
using Junevy.Communication.Omron.Protocol;
using Junevy.Communication.Testing;
using static Junevy.Communication.Omron.Tests.FinsTestSupport;
using static Junevy.Communication.Testing.HexVector;

namespace Junevy.Communication.Omron.Tests;

/// <summary>
/// 节点地址握手（计划 Task 13 第 2 条、P4-D9、P4-D18）：一帧节点地址请求，应答的两个节点号连同选项里的网络号与单元号
/// 组成路由写入会话；头错误码非 0 为 <see cref="CommErrorKind.RemoteError"/>、帧结构不符为
/// <see cref="CommErrorKind.ProtocolViolation"/>，通道失败原样返回，全部走结果而不抛异常。请求只有一步，时限就是通道的请求超时。
/// </summary>
public sealed class FinsNodeHandshakeTests
{
    [Fact]
    public async Task Handshake_RequestsAnAutoAssignedNodeAndStoresBothRespondedNodes()
    {
        var session = new FinsSession();
        var channel = new ScriptedByteChannel(_ => CommResult<byte[]>.Success(NodeAck(ClientNode, ServerNode)));

        Succeeded(await Initialize(session, channel, Settings()));

        // 请求帧没有 SID 之类的可变量，可以与 V-F-1 逐字节比较（体里的 4 字节节点号为 0 = 自动分配）。
        Assert.Equal(V_F_1, ToText(channel.Requests[0]));
        FinsRoute route = session.Current.Route!.Value;
        Assert.Equal(ClientNode, route.SourceNode);
        Assert.Equal(ServerNode, route.DestinationNode);
    }

    [Fact]
    public async Task Handshake_WithAFixedClientNode_RequestsThatNumber()
    {
        // P4-D20 ⑥：固定节点号的写法只体现在请求体里，应答形状与自动分配相同。
        var session = new FinsSession();
        var channel = new ScriptedByteChannel(_ => CommResult<byte[]>.Success(NodeAck(ClientNode, ServerNode)));

        Succeeded(await Initialize(session, channel, Settings(0x0B)));

        Assert.Equal(NodeRequest_FixedClientNode, ToText(channel.Requests[0]));
    }

    [Fact]
    public async Task Handshake_WithAFixedClientNode_StoresTheNodeNumberFromTheResponse()
    {
        // 以应答为准：PLC 回的是 0x0C 时 SA1 用 0x0C，客户端不假设自己请求的号一定被接受（P4-D20 ⑥）。
        var session = new FinsSession();
        var channel = new ScriptedByteChannel(_ => CommResult<byte[]>.Success(NodeAck(0x0C, ServerNode)));

        Succeeded(await Initialize(session, channel, Settings(0x0B)));

        Assert.Equal(0x0C, session.Current.Route!.Value.SourceNode);
    }

    [Fact]
    public async Task Handshake_TakesTheNetworkAndUnitFromTheOptions()
    {
        // P4-D18：DNA 与 DA2 来自选项，SNA 与 SA2 固定为 0，DA1/SA1 来自应答。
        var session = new FinsSession();
        var channel = new ScriptedByteChannel(_ => CommResult<byte[]>.Success(NodeAck(ClientNode, ServerNode)));

        Succeeded(await Initialize(session, channel, FinsProtocolSettings.Create(0, 3, 4)));

        FinsRoute route = session.Current.Route!.Value;
        Assert.Equal(3, route.DestinationNetwork);
        Assert.Equal(4, route.DestinationUnit);
        Assert.Equal(0, route.SourceNetwork);
        Assert.Equal(0, route.SourceUnit);
    }

    [Fact]
    public async Task Handshake_UsesTheChannelRequestTimeout()
    {
        var session = new FinsSession();
        var channel = new ScriptedByteChannel(_ => CommResult<byte[]>.Success(NodeAck(ClientNode, ServerNode)));

        Succeeded(await Initialize(session, channel, Settings()));

        // 计划 Task 8 第 3 条的同一口径：握手不自带时限。
        Assert.Null(channel.LastOptions);
    }

    [Fact]
    public async Task Handshake_HeaderError_FailsAsRemoteErrorAndLeavesTheSessionEmpty()
    {
        var session = new FinsSession();
        var channel = new ScriptedByteChannel(_ => CommResult<byte[]>.Success(WithTcpError(V_F_2, 2)));

        CommResult result = await Initialize(session, channel, Settings());

        AssertRemoteError(result, 2, TcpErrorMessage(2));
        Assert.Null(session.Current.Route);
    }

    [Theory]
    [InlineData(V_F_3)]
    [InlineData(V_F_5_Response)]
    public async Task Handshake_ResponseIsNotANodeAddressReply_FailsAsViolation(string frame)
    {
        // 命令字段为 2（FINS 帧）而不是 1；结束码形状的帧同样没有节点体。
        var session = new FinsSession();
        var channel = new ScriptedByteChannel(_ => CommResult<byte[]>.Success(ToBytes(frame)));

        AssertViolation(await Initialize(session, channel, Settings()));
        Assert.Null(session.Current.Route);
    }

    [Fact]
    public async Task Handshake_NodeBodyIsTruncated_FailsAsViolation()
    {
        var session = new FinsSession();
        byte[] truncated = Truncated(V_F_2, 20);
        var channel = new ScriptedByteChannel(_ => CommResult<byte[]>.Success(truncated));

        AssertViolation(await Initialize(session, channel, Settings()));
    }

    [Fact]
    public async Task Handshake_NodeNumberBeyondOneByte_FailsAsViolation()
    {
        // 节点号写进 1 字节的 SA1，体里的高位非 0 无法表达，按结构错误处理（Task 12 偏差 4）。
        var session = new FinsSession();
        byte[] frame = Mutated(V_F_2, ClientNodeTailIndex - 1, 0x01);
        var channel = new ScriptedByteChannel(_ => CommResult<byte[]>.Success(frame));

        AssertViolation(await Initialize(session, channel, Settings()));
    }

    [Theory]
    [InlineData(CommErrorKind.Timeout)]
    [InlineData(CommErrorKind.ConnectionClosed)]
    public async Task Handshake_ChannelFails_PropagatesTheKind(CommErrorKind kind)
    {
        var session = new FinsSession();
        var channel = new ScriptedByteChannel(_ => CommResult<byte[]>.Fail("The scripted channel has no reply.", kind));

        CommResult result = await Initialize(session, channel, Settings());

        Assert.Equal(kind, result.ErrorKind);
        Assert.Single(channel.Requests);
        Assert.Null(session.Current.Route);
    }

    [Fact(Timeout = 20000)]
    public async Task Reconnect_HandshakesAgainAndTakesTheNewNodeNumber()
    {
        // 经真实通道验证接线：分帧、Sequential 关联与初始化器都来自 FinsProtocolProfile；每次打开都重新握手。
        var session = new FinsSession();
        ChannelComponents components = FinsProtocolProfile.Configure(
            new ChannelComponents(), Settings(), session, ChannelDelivery.Stream);
        var requests = new ConcurrentQueue<byte[]>();
        var handshakes = 0;

        var channel = new DuplexClientChannel(new ClientChannelSettings { HandshakeTimeout = 4000, RequestTimeout = 3000 }, components, pair =>
        {
            byte clientNode = Interlocked.Increment(ref handshakes) == 1 ? ClientNode : (byte)0x0C;
            _ = new DeviceSimulator(FinsFraming.Create(),
                frame =>
                {
                    requests.Enqueue(frame);
                    return NodeAck(clientNode, ServerNode);
                }).RunAsync(pair.B, CancellationToken.None);
            return Task.CompletedTask;
        });
        await using var owned = channel;

        Succeeded(await channel.ConnectAsync());
        Assert.Equal(ClientNode, session.Current.Route!.Value.SourceNode);

        await channel.DisconnectAsync();
        Succeeded(await channel.ConnectAsync());

        Assert.Equal(0x0C, session.Current.Route!.Value.SourceNode);
        Assert.Equal(2, Volatile.Read(ref handshakes));
        Assert.Equal(2, requests.Count(IsNodeRequest));
    }

    private static bool IsNodeRequest(byte[] frame) => FinsTcpFrame.TryUnwrap(frame, out uint command, out _, out _, out _)
        && command == FinsTcpFrame.CommandNodeRequest;

    private static Task<CommResult> Initialize(FinsSession session, IByteChannel channel, FinsProtocolSettings settings)
        => new FinsNodeHandshake(settings, session).InitializeAsync(channel, CancellationToken.None);

    private static FinsProtocolSettings Settings(byte clientNode = 0) => FinsProtocolSettings.Create(clientNode, 0, 0);
}
