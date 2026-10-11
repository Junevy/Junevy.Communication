using System.Collections.Concurrent;
using Junevy.Communication.Channels;
using Junevy.Communication.Core.Results;
using Junevy.Communication.Siemens.Framing;
using Junevy.Communication.Testing;
using static Junevy.Communication.Siemens.Tests.S7TestSupport;
using static Junevy.Communication.Testing.HexVector;

namespace Junevy.Communication.Siemens.Tests;

/// <summary>
/// 握手（计划第 12 节第 3 条、P4-D8、P4-D9、P4-D13）：COTP CR → CC → Setup Communication → 协商的 PDU 长度写入会话；
/// 每一步失败都返回带原因的结果而不是抛异常，超时交给通道的请求超时（不另设时限）。
/// </summary>
public sealed class S7HandshakeTests
{
    [Theory]
    [InlineData(V_S7_4, 240)]
    [InlineData(Setup_Ack_960, 960)]
    public async Task Handshake_ConnectsThenNegotiatesThePdu(string setupAck, int negotiated)
    {
        var session = new S7Session();
        var channel = Cpu(setupAck);

        Succeeded(await Initialize(session, channel));

        // CR 没有引用号，可以与 V-S7-1 逐字节比较；Setup 的引用号来自进程级计数器，只比较回显后的向量。
        Assert.Equal(V_S7_1, ToText(channel.Requests[0]));
        Assert.Equal(WithReference(V_S7_3, ReferenceOf(channel.Requests[1])), channel.Requests[1]);
        Assert.Equal(negotiated, session.Current.NegotiatedPduBytes);

        // 计划第 12 节第 3 条：两步都用通道的请求超时，握手不自带时限。
        Assert.Null(channel.LastOptions);
    }

    [Theory]
    [InlineData(Cc_WrongPduType)]
    [InlineData(V_S7_5)]
    [InlineData(V_S7_3)]
    public async Task Handshake_ConfirmIsNotConnectionConfirm_Fails(string confirm)
    {
        // 应答一律是 <see cref="Cc_WrongPduType" /> 这类非 CC 帧：CR 一步就失败，Setup 不会发出。
        var session = new S7Session();
        var channel = new ScriptedByteChannel(_ => CommResult<byte[]>.Success(ToBytes(confirm)));

        CommResult result = await Initialize(session, channel);

        AssertViolation(result);
        Assert.Null(session.Current.NegotiatedPduBytes);
        Assert.Single(channel.Requests);
    }

    [Fact]
    public async Task Handshake_SetupHeaderError_FailsAsRemoteError()
    {
        var session = new S7Session();
        var channel = Cpu(V_S7_12);

        CommResult result = await Initialize(session, channel);

        AssertRemoteError(result, 0x8104, "S7 error class 0x81 code 0x04. " + PutGetHint);
        Assert.Null(session.Current.NegotiatedPduBytes);
    }

    [Theory]
    [InlineData(Setup_Ack_PduTooSmall, "below the minimum")]
    [InlineData(Setup_Ack_ShortParameters, "expected 8")]
    [InlineData(V_S7_8, "expected 8")]
    public async Task Handshake_NegotiationResultIsUnusable_Fails(string setupAck, string messagePart)
    {
        // P4-D13：协商值 32 小于下限 64；另两条是参数块形状不符。
        var session = new S7Session();
        var channel = Cpu(setupAck);

        CommResult result = await Initialize(session, channel);

        AssertViolation(result);
        Assert.Contains(messagePart, result.ErrorMessage, StringComparison.Ordinal);
        Assert.Null(session.Current.NegotiatedPduBytes);
    }

    [Theory]
    [InlineData(CommErrorKind.Timeout)]
    [InlineData(CommErrorKind.ConnectionClosed)]
    public async Task Handshake_ChannelFailsDuringConnectionRequest_PropagatesTheKind(CommErrorKind kind)
    {
        var session = new S7Session();
        var channel = new ScriptedByteChannel(_ => Failure(kind));

        CommResult result = await Initialize(session, channel);

        Assert.Equal(kind, result.ErrorKind);
        Assert.Single(channel.Requests);
        Assert.Null(session.Current.NegotiatedPduBytes);
    }

    [Theory]
    [InlineData(CommErrorKind.Timeout)]
    [InlineData(CommErrorKind.ConnectionClosed)]
    public async Task Handshake_ChannelFailsDuringSetup_PropagatesTheKind(CommErrorKind kind)
    {
        var session = new S7Session();
        var channel = new ScriptedByteChannel(request => IsConnectionRequest(request)
            ? CommResult<byte[]>.Success(ToBytes(V_S7_2))
            : Failure(kind));

        CommResult result = await Initialize(session, channel);

        Assert.Equal(kind, result.ErrorKind);
        Assert.Equal(2, channel.Requests.Count);
        Assert.Null(session.Current.NegotiatedPduBytes);
    }

    [Fact(Timeout = 20000)]
    public async Task Reconnect_HandshakesAgain()
    {
        // 经真实通道验证接线：分帧、Sequential 关联与初始化器都来自 S7ProtocolProfile，重连后重新握手并写入新的协商值。
        var session = new S7Session();
        ChannelComponents components = S7ProtocolProfile.Configure(new ChannelComponents(), Settings(), session, ChannelDelivery.Stream);
        var requests = new ConcurrentQueue<byte[]>();
        int negotiations = 0;

        var channel = new DuplexClientChannel(HandshakeSettings(), components, pair =>
        {
            string setupAck = Interlocked.Increment(ref negotiations) == 1 ? V_S7_4 : Setup_Ack_960;
            _ = new DeviceSimulator(S7Framing.Create(), frame => Reply(frame, setupAck, requests)).RunAsync(pair.B, CancellationToken.None);
            return Task.CompletedTask;
        });
        await using var owned = channel;

        Succeeded(await channel.ConnectAsync());
        Assert.Equal(240, session.Current.NegotiatedPduBytes);

        await channel.DisconnectAsync();
        Succeeded(await channel.ConnectAsync());

        Assert.Equal(960, session.Current.NegotiatedPduBytes);
        Assert.Equal(2, Volatile.Read(ref negotiations));
        Assert.Equal(2, requests.Count(frame => IsConnectionRequest(frame)));
    }

    private static byte[]? Reply(byte[] frame, string setupAck, ConcurrentQueue<byte[]> requests)
    {
        requests.Enqueue(frame);
        return IsConnectionRequest(frame)
            ? ToBytes(V_S7_2)
            : WithReference(setupAck, ReferenceOf(frame));
    }

    private static bool IsConnectionRequest(byte[] frame) => frame[CotpPduType] == ConnectionRequestCode;

    private static ScriptedByteChannel Cpu(string setupAck)
        => new ScriptedByteChannel(request => CommResult<byte[]>.Success(IsConnectionRequest(request)
            ? ToBytes(V_S7_2)
            : WithReference(setupAck, ReferenceOf(request))));

    private static CommResult<byte[]> Failure(CommErrorKind kind)
        => CommResult<byte[]>.Fail("The scripted channel has no reply.", kind);

    private static Task<CommResult> Initialize(S7Session session, IByteChannel channel)
        => new S7Handshake(Settings(), session).InitializeAsync(channel, CancellationToken.None);

    private static S7ProtocolSettings Settings() => S7ProtocolSettings.Create(Pg, Rack, Slot);

    private static ClientChannelSettings HandshakeSettings() => new ClientChannelSettings { HandshakeTimeout = 4000, RequestTimeout = 3000 };
}
