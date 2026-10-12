using Junevy.Communication.Core.Results;
using Junevy.Communication.Omron.Protocol;
using Junevy.Communication.Testing;
using static Junevy.Communication.Omron.Tests.FinsTestSupport;
using static Junevy.Communication.Testing.HexVector;

namespace Junevy.Communication.Omron.Tests;

/// <summary>
/// 心跳探测（计划 Task 13 第 4 条、P4-D15）：读 D0 的 1 字；只要拿到结构正确、SID 相符的应答就算健康，
/// 结束码不参与判定；FINS/TCP 头的错误码仍按 P4-D14 判为设备侧错误，超时等通道失败原样返回，由通道按 MaxFailures 计数。
/// </summary>
public sealed class FinsReadProbeTests
{
    [Fact]
    public async Task Probe_ReadsOneWordOfDataMemoryZero()
    {
        var channel = new ScriptedByteChannel(request => CommResult<byte[]>.Success(ReadAck(SessionIdOf(request), Payload)));

        Succeeded(await ProbeAsync(channel));

        Assert.Equal(WithSessionId(Probe_ReadD0, SessionIdOf(channel.Requests[0])), channel.Requests[0]);
        Assert.Null(channel.LastOptions);
    }

    [Fact]
    public async Task Probe_UsesTheWordAreaCodeOfTheDataDeviceFromTheTable()
    {
        // 探测的区域代码与软元件表同源，避免两处各写一遍 0x82（17.3 节）。
        byte tableCode = FinsDeviceTable.Devices.Single(device => device.Symbol == "D").WordAreaCode;
        var channel = new ScriptedByteChannel(request => CommResult<byte[]>.Success(ReadAck(SessionIdOf(request), Payload)));

        Succeeded(await ProbeAsync(channel));

        Assert.Equal(tableCode, channel.Requests[0][AreaCodeIndex]);
    }

    [Theory]
    [InlineData(V_F_4)]
    [InlineData(V_F_8)]
    [InlineData(V_F_9)]
    public async Task Probe_AnyWellFormedResponseIsHealthy(string vector)
    {
        // V-F-8 的结束码是 0x1103、V-F-9 屏蔽后成功：设备回了话，链路与协议栈都正常（P4-D15）。
        var channel = new ScriptedByteChannel(request => CommResult<byte[]>.Success(ReadLike(vector, SessionIdOf(request))));

        Succeeded(await ProbeAsync(channel));
    }

    [Fact]
    public async Task Probe_WrongCommandEcho_IsProtocolViolation()
    {
        // V-F-5 的应答回显的是写命令 0102；探测发的是读请求，命令码不一致说明这不是探测的应答。
        var channel = new ScriptedByteChannel(request => CommResult<byte[]>.Success(ReadLike(V_F_5_Response, SessionIdOf(request))));

        AssertViolation(await ProbeAsync(channel));
    }

    [Fact]
    public async Task Probe_HeaderError_FailsAsRemoteError()
    {
        // P4-D14：外壳错误码非 0 是设备侧错误，探测不因它判健康。
        const uint error = 2;
        var channel = new ScriptedByteChannel(_ => CommResult<byte[]>.Success(WithTcpError(V_F_4, error)));

        AssertRemoteError(await ProbeAsync(channel), error, TcpErrorMessage(error));
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
    [InlineData(V_F_1)]
    [InlineData(V_F_3)]
    public async Task Probe_MalformedResponse_IsProtocolViolation(string vector)
    {
        // 空串是一帧垃圾字节；V-F-1 是节点地址请求（命令 0）；V-F-3 是请求帧而不是应答（ICF 没有应答位）。
        byte[] garbage = vector.Length == 0 ? new byte[] { 0x00, 0x11, 0x22, 0x33 } : ToBytes(vector);
        var channel = new ScriptedByteChannel(_ => CommResult<byte[]>.Success(garbage));

        AssertViolation(await ProbeAsync(channel));
    }

    [Fact]
    public async Task Probe_SessionIdMismatch_IsProtocolViolation()
    {
        var channel = new ScriptedByteChannel(request => CommResult<byte[]>.Success(
            Mutated(V_F_4, SidIndex, unchecked((byte)(SessionIdOf(request) + 1)))));

        AssertViolation(await ProbeAsync(channel));
    }

    [Fact]
    public async Task Probe_BeforeTheNodeHandshake_FailsAsUnspecified()
    {
        // 没有节点号就发不出请求；这是接线错误而不是设备错误，因此不占用"应答不符"的分类。
        var channel = new ScriptedByteChannel(request => CommResult<byte[]>.Success(ReadAck(SessionIdOf(request), Payload)));

        CommResult result = await ProbeAsync(channel, new FinsSession());

        Assert.False(result.IsSuccess);
        Assert.Equal(CommErrorKind.Unspecified, result.ErrorKind);
        Assert.Empty(channel.Requests);
    }

    private static byte[] Payload => new byte[FinsMemoryArea.WordBytes];

    private static byte[] ReadLike(string vector, byte sessionId) => Mutated(vector, SidIndex, sessionId);

    private static Task<CommResult> ProbeAsync(ScriptedByteChannel channel, FinsSession? session = null)
        => new FinsReadProbe(channel, Session(session)).ProbeAsync(CancellationToken.None);

    private static FinsSession Session(FinsSession? session)
    {
        if (session is not null)
            return session;

        var handshake = new FinsSession();
        handshake.ReplaceAfterHandshake(Route);
        return handshake;
    }
}
