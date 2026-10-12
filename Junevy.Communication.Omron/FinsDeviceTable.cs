using Junevy.Communication.Plc;

namespace Junevy.Communication.Omron;

/// <summary>
/// FINS 软元件表（P4-D16）：一份声明式列表，每行一个符号，区域代码对照计划第 17.3 节。
/// EM 存储区展开为 <c>E0_</c>–<c>EC_</c> 十三个符号，组号是一位十六进制数字，区域代码为"字 0xA0 + 组号 / 位 0x20 + 组号"
/// （P4-D20 ④：该展开未经真机验证）。不收录定时器与计数器区（P4-D1 的范围决定）。
/// </summary>
internal static class FinsDeviceTable
{
    /// <summary>软元件表。字段顺序：符号、字区代码、位区代码（第 17.3 节）。</summary>
    public static readonly IReadOnlyList<FinsDevice> Devices = new[]
    {
        new FinsDevice("CIO", 0xB0, 0x30),
        new FinsDevice("W", 0xB1, 0x31),
        new FinsDevice("H", 0xB2, 0x32),
        new FinsDevice("A", 0xB3, 0x33),
        new FinsDevice("D", 0x82, 0x02),
        new FinsDevice("E0_", 0xA0, 0x20),
        new FinsDevice("E1_", 0xA1, 0x21),
        new FinsDevice("E2_", 0xA2, 0x22),
        new FinsDevice("E3_", 0xA3, 0x23),
        new FinsDevice("E4_", 0xA4, 0x24),
        new FinsDevice("E5_", 0xA5, 0x25),
        new FinsDevice("E6_", 0xA6, 0x26),
        new FinsDevice("E7_", 0xA7, 0x27),
        new FinsDevice("E8_", 0xA8, 0x28),
        new FinsDevice("E9_", 0xA9, 0x29),
        new FinsDevice("EA_", 0xAA, 0x2A),
        new FinsDevice("EB_", 0xAB, 0x2B),
        new FinsDevice("EC_", 0xAC, 0x2C),
    };

    /// <summary>软元件地址解析器。编号与位号都按十进制解析（例如 <c>W10.03</c>，P4-D16），即解析器的默认进制。</summary>
    public static readonly PlcAddressParser<FinsDevice, FinsAddress> AddressParser = new PlcAddressParser<FinsDevice, FinsAddress>(
        Devices,
        (device, number, bit, text) => new FinsAddress(device, number, bit, text));
}
