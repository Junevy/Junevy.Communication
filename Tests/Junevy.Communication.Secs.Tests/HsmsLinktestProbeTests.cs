using Junevy.Communication.Channels;
using Junevy.Communication.Core.Results;
using Junevy.Communication.Secs.Hsms;
using Junevy.Communication.Secs.Protocol;
using Junevy.Communication.Testing;
using Microsoft.Extensions.Logging;

namespace Junevy.Communication.Secs.Tests;

/// <summary>
/// Linktest 探测（P51-D9、Q33）：发 <c>Linktest.req</c>（超时 T6），只接受 <c>Linktest.rsp</c>。
/// 用例只碰字节，因此用脚本化通道；真实链路上的调度见 <see cref="HsmsLinktestHeartbeatTests"/>。
/// </summary>
public sealed class HsmsLinktestProbeTests
{
    private static readonly HsmsEquipmentTimers Timers = HsmsTimers.Fast(t6: 400);

    [Fact]
    public async Task Probe_SendsALinktestRequestAndAcceptsALinktestResponse()
    {
        var channel = new ScriptedByteChannel(AnswerWithLinktest);
        var probe = new HsmsLinktestProbe(channel, Timers);

        CommResult result = await probe.ProbeAsync(CancellationToken.None);

        Assert.True(result.IsSuccess);

        // V-H-4 的形状：会话号 0xFFFF、SType 5；System Bytes 由库里的进程级计数器产生。
        byte[] request = Assert.IsType<byte[]>(channel.LastRequest);
        Assert.Equal(
            HsmsTestFrames.WithSystemBytes("00 00 00 0A FF FF 00 00 00 05 00 00 00 07", HsmsTestFrames.Parse(request).SystemBytes),
            HexVector.ToText(request));
        RequestOptions options = Assert.IsType<RequestOptions>(channel.LastOptions);
        Assert.Equal(Timers.T6, options.Timeout);
    }

    [Fact]
    public async Task Probe_Fails_WhenTheAnswerIsNotALinktestResponse()
    {
        // 主机把 Linktest.req 换成 Select.rsp 应答：这不是链路测试应答。
        var channel = new ScriptedByteChannel(request =>
            CommResult<byte[]>.Success(HsmsControl.SelectResponse(HsmsTestFrames.Parse(request).SystemBytes, 0).ToArray()));
        var probe = new HsmsLinktestProbe(channel, Timers);

        CommResult result = await probe.ProbeAsync(CancellationToken.None);

        Assert.False(result.IsSuccess);
        Assert.Equal(CommErrorKind.ProtocolViolation, result.ErrorKind);
        Assert.Contains("SType 2", result.ErrorMessage);
    }

    [Fact]
    public async Task Probe_Fails_WhenTheAnswerCannotBeParsed()
    {
        var channel = new ScriptedByteChannel(request => CommResult<byte[]>.Success(HexVector.Truncated("00 00 00 0A FF FF 00 00 00 06 00 00 00 07", 8)));
        var probe = new HsmsLinktestProbe(channel, Timers);

        CommResult result = await probe.ProbeAsync(CancellationToken.None);

        Assert.False(result.IsSuccess);
        Assert.Equal(CommErrorKind.ProtocolViolation, result.ErrorKind);
    }

    [Fact]
    public async Task Probe_PassesTheChannelsFailureThrough()
    {
        var channel = new ScriptedByteChannel(request => CommResult<byte[]>.Fail("No linktest reply.", CommErrorKind.Timeout));
        var probe = new HsmsLinktestProbe(channel, Timers);

        CommResult result = await probe.ProbeAsync(CancellationToken.None);

        Assert.False(result.IsSuccess);
        Assert.Equal(CommErrorKind.Timeout, result.ErrorKind);
    }

    // 主机侧的行为：把 System Bytes 原样回送（V-H-5）。
    private static CommResult<byte[]> AnswerWithLinktest(byte[] request)
    {
        HsmsMessage message = HsmsTestFrames.Parse(request);
        return CommResult<byte[]>.Success(HsmsControl.LinktestResponse(message.SystemBytes).ToArray());
    }
}

/// <summary>
/// Linktest 探测在真实链路上的调度（P51-D9）：心跳只在空闲时探测，超时用 T6，<c>MaxFailures = 1</c>。
/// </summary>
[Collection(SocketTimingCollection.Name)]
public sealed class HsmsLinktestHeartbeatTests
{
    [Fact(Timeout = 30000)]
    public async Task WithALinktestInterval_TheSelectedSessionIsProbedAndTheLinkSurvives()
    {
        await using HsmsPassiveRig rig = await HsmsPassiveRig.StartAsync(HsmsTimers.Fast(linktestInterval: 150));
        await using HsmsHostConnector host = await HsmsHostConnector.ConnectAsync(rig.Port);

        await host.SendAsync(HsmsControl.SelectRequest(1).ToArray());
        Assert.Equal("00 00 00 0A FF FF 00 00 00 02 00 00 00 01", (await host.NextFrameAsync(5000)).Text);

        // 主机应答探测（V-H-4 → V-H-5，System Bytes 原样回送）。
        HsmsHostFrame request = await host.NextFrameAsync(5000);
        Assert.Equal(HsmsControlKind.LinktestRequest, request.Message.Kind);
        await host.SendAsync(HsmsControl.LinktestResponse(request.Message.SystemBytes).ToArray());

        // 应答被认领之后，主机收到的是下一次探测（新的 System Bytes），而不是断链。
        HsmsHostFrame next = await host.NextFrameAsync(5000);
        Assert.Equal(HsmsControlKind.LinktestRequest, next.Message.Kind);
        Assert.NotEqual(request.Message.SystemBytes, next.Message.SystemBytes);

        // 探测成功：会话保持已选中，链路没有被心跳判死。
        Assert.Equal(HsmsState.Selected, rig.Session.State);
        Assert.Equal(1, rig.Server.SessionCount);
    }

    [Fact(Timeout = 30000)]
    public async Task WhenTheHostStopsAnsweringProbes_TheSessionIsTornDown()
    {
        // MaxFailures = 1：一次探测失败就断链（P51-D9）。
        await using HsmsPassiveRig rig = await HsmsPassiveRig.StartAsync(HsmsTimers.Fast(linktestInterval: 150, t6: 300));
        await using HsmsHostConnector host = await HsmsHostConnector.ConnectAsync(rig.Port);

        await host.SendAsync(HsmsControl.SelectRequest(1).ToArray());
        await host.NextFrameAsync(5000);
        await Polling.WaitUntilAsync(() => host.ReceivedCount >= 1, 5000);

        // 之后不再应答 Linktest.req：设备端在 T6 之后断开连接。
        await host.AwaitPeerCloseAsync(5000);
        Assert.Equal(0, rig.Server.SessionCount);
    }
}
