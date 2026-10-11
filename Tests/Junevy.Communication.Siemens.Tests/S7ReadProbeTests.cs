using Junevy.Communication.Core.Results;
using Junevy.Communication.Testing;
using static Junevy.Communication.Siemens.Tests.S7TestSupport;
using static Junevy.Communication.Testing.HexVector;

namespace Junevy.Communication.Siemens.Tests;

/// <summary>
/// 心跳探测（计划第 12 节第 4 条、P4-D15）：读 MB0 的 1 字节；只要拿到结构正确、引用号相符的应答就算健康，
/// 数据项返回码与应答头错误码都不影响判定；没有应答或帧结构不符则判为失败。
/// </summary>
public sealed class S7ReadProbeTests
{
    [Fact]
    public async Task Probe_ReadsOneByteOfMerkerZero()
    {
        var channel = new ScriptedByteChannel(request => Ack(V_S7_10_Ack, request));

        await ProbeAsync(channel);

        byte[] request = channel.Requests[0];
        Assert.Equal(WithReference(Probe_ReadM0, ReferenceOf(request)), request);
        Assert.Null(channel.LastOptions);
    }

    [Theory]
    [InlineData(V_S7_10_Ack)]
    [InlineData(V_S7_11)]
    [InlineData(V_S7_12)]
    public async Task Probe_AnyWellFormedAckIsHealthy(string ack)
    {
        // V-S7-11 的返回码是 0x0A、V-S7-12 的应答头是 0x8104：设备回了话，链路与协议栈都正常。
        var channel = new ScriptedByteChannel(request => Ack(ack, request));

        Succeeded(await ProbeAsync(channel));
    }

    [Theory]
    [InlineData(CommErrorKind.Timeout)]
    [InlineData(CommErrorKind.ConnectionClosed)]
    [InlineData(CommErrorKind.Cancelled)]
    public async Task Probe_WithoutResponse_FailsWithTheChannelKind(CommErrorKind kind)
    {
        var channel = new ScriptedByteChannel(_ => CommResult<byte[]>.Fail("The scripted channel has no reply.", kind));

        CommResult result = await ProbeAsync(channel);

        Assert.False(result.IsSuccess);
        Assert.Equal(kind, result.ErrorKind);
    }

    [Theory]
    [InlineData("")]
    [InlineData(V_S7_1)]
    [InlineData(V_S7_2)]
    public async Task Probe_MalformedResponse_IsProtocolViolation(string frame)
    {
        // 空串是一帧垃圾字节；V-S7-1、V-S7-2 是 COTP 帧而不是 S7 应答，都不足以证明协议栈正常。
        byte[] garbage = frame.Length == 0 ? new byte[] { 0x00, 0x11, 0x22, 0x33 } : ToBytes(frame);
        var channel = new ScriptedByteChannel(_ => CommResult<byte[]>.Success(garbage));

        AssertViolation(await ProbeAsync(channel));
    }

    [Fact]
    public async Task Probe_ReferenceMismatch_IsProtocolViolation()
    {
        var channel = new ScriptedByteChannel(request => CommResult<byte[]>.Success(
            WithReference(V_S7_10_Ack, unchecked((ushort)(ReferenceOf(request) + 1)))));

        AssertViolation(await ProbeAsync(channel));
    }

    private static CommResult<byte[]> Ack(string vector, byte[] request)
        => CommResult<byte[]>.Success(WithReference(vector, ReferenceOf(request)));

    private static async Task<CommResult> ProbeAsync(ScriptedByteChannel channel)
        => await new S7ReadProbe(channel).ProbeAsync(CancellationToken.None);
}
