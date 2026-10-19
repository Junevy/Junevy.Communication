using Junevy.Communication.Plc;

namespace Junevy.Communication.Modbus.Tests;

/// <summary>
/// 端到端测试共用的场景。TCP、UDP、串口用同一组断言验证同一个从站布局，避免三份相同的断言。
/// </summary>
internal static class ModbusScenarios
{
    /// <summary>
    /// 四个区域的往返：保持寄存器的字写读、输入寄存器读、线圈写读、离散输入读。
    /// 调用前从站内存须已设置 <c>IR5 = 0x0042</c> 与 <c>DI7 = 1</c>。
    /// </summary>
    public static async Task FourAreasRoundTripAsync(ModbusClient client)
    {
        Assert.True((await client.WriteUInt16Async("HR10", 0x1234)).IsSuccess);
        Assert.Equal((ushort)0x1234, (await client.ReadUInt16Async("HR10")).Data);
        Assert.Equal((ushort)0x0042, (await client.ReadUInt16Async("IR5")).Data);
        Assert.True((await client.WriteBoolAsync("C3", true)).IsSuccess);
        Assert.True((await client.ReadBoolAsync("C3")).Data);
        Assert.True((await client.ReadBoolAsync("DI7")).Data);
    }
}
