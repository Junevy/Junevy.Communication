using Junevy.Communication.Omron.Protocol;
using static Junevy.Communication.Omron.Tests.FinsTestSupport;
using static Junevy.Communication.Testing.HexVector;

namespace Junevy.Communication.Omron.Tests;

/// <summary>
/// 设备替身的自检（计划第 18 节自查项：替身也要有证据）：直接调用 <see cref="FinsDeviceSimulator.Handle"/>，
/// 证明它的应答与 17.2 节的向量逐字节一致。字读路径由端到端用例覆盖，位读路径在这里单独证明——
/// P4-D6 之后客户端不会发出位读请求（读位改为读覆盖的字），替身的那条分支只能自己验证。
/// </summary>
public sealed class FinsDeviceSimulatorTests
{
    [Fact]
    public void HandlerAnswersWordReadsWithTheV_F_4Bytes()
    {
        var device = new FinsDeviceSimulator();
        device.Memory.SetWords("D", 100, 0x1234, 0x5678);

        byte[] request = FinsMemoryArea.BuildReadRequest(Route, sessionId: 0x01, DWordArea, wordAddress: 100, bitNumber: 0, count: 2);

        Assert.Equal(ToBytes(V_F_3), request);
        Assert.Equal(ToBytes(V_F_4), device.Handle(request)!);
    }

    [Fact]
    public void HandlerAnswersBitReadsWithOneBytePerPoint()
    {
        var device = new FinsDeviceSimulator();
        device.Memory.SetWords("CIO", 0, 0x0005);   // 位 0 与位 2 置起（P4-D6：位 0 是大端字的最低位）

        byte[] request = FinsMemoryArea.BuildReadRequest(Route, sessionId: 0x03, CioBitArea, wordAddress: 0, bitNumber: 0, count: 3);

        Assert.Equal(ToBytes(V_F_6_Request), request);
        byte[] reply = device.Handle(request)!;
        Assert.Equal(ToBytes(V_F_6_Response), reply);
        Assert.Equal(ToBytes("01 00 01"), Succeeded(FinsMemoryArea.ReadResponse(reply, 0x03, payloadBytes: 3, logger: null)));
    }

    [Fact]
    public void MemoryAccessOutsideTheArea_FailsLoudly()
    {
        // 用例预置或核对时写错地址，必须当场失败，而不是静默不写或读出空数组。
        var device = new FinsDeviceSimulator();

        Assert.Throws<InvalidOperationException>(() => device.Memory.SetWords("D", FinsDeviceSimulator.AreaCapacity, 0x0001));
        Assert.Throws<InvalidOperationException>(() => device.Memory.GetWords("D", FinsDeviceSimulator.AreaCapacity - 1, 2));
        Assert.Throws<InvalidOperationException>(() => device.Memory.SetPoint("W", FinsDeviceSimulator.AreaCapacity * 16, true));
    }
}
