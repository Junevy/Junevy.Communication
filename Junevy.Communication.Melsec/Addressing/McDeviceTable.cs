using Junevy.Communication.Plc;

namespace Junevy.Communication.Melsec;

/// <summary>
/// MELSEC Q/L 兼容的软元件表（P2-D2）：一份声明式列表，每行一个软元件。
/// 符号、单位、进制与代码对照手册 p.67–68 的软元件代码表（8.1 节）。ZR 的编号为十六进制（手册 p.68，"Serial number access method"）。
/// 不收录 iQ-R 专用的长计时器、长计数器、LZ、RD，以及需要扩展指定（子命令 008x）的 U\G 与链接直接软元件（P2-D2）。
/// </summary>
internal static class McDeviceTable
{
    private const PlcAddressUnit Bit = PlcAddressUnit.Bit;
    private const PlcAddressUnit Word = PlcAddressUnit.Word;

    /// <summary>软元件表。字段顺序：符号、单位、进制、ASCII 代码、二进制代码。</summary>
    public static readonly IReadOnlyList<McDevice> Devices = new[]
    {
        new McDevice("SM", Bit, 10, "SM", 0x91),
        new McDevice("SD", Word, 10, "SD", 0xA9),
        new McDevice("X", Bit, 16, "X*", 0x9C),
        new McDevice("Y", Bit, 16, "Y*", 0x9D),
        new McDevice("M", Bit, 10, "M*", 0x90),
        new McDevice("L", Bit, 10, "L*", 0x92),
        new McDevice("F", Bit, 10, "F*", 0x93),
        new McDevice("V", Bit, 10, "V*", 0x94),
        new McDevice("B", Bit, 16, "B*", 0xA0),
        new McDevice("D", Word, 10, "D*", 0xA8),
        new McDevice("W", Word, 16, "W*", 0xB4),
        new McDevice("TS", Bit, 10, "TS", 0xC1),
        new McDevice("TC", Bit, 10, "TC", 0xC0),
        new McDevice("TN", Word, 10, "TN", 0xC2),
        new McDevice("SS", Bit, 10, "SS", 0xC7),
        new McDevice("SC", Bit, 10, "SC", 0xC6),
        new McDevice("SN", Word, 10, "SN", 0xC8),
        new McDevice("CS", Bit, 10, "CS", 0xC4),
        new McDevice("CC", Bit, 10, "CC", 0xC3),
        new McDevice("CN", Word, 10, "CN", 0xC5),
        new McDevice("SB", Bit, 16, "SB", 0xA1),
        new McDevice("SW", Word, 16, "SW", 0xB5),
        new McDevice("S", Bit, 10, "S*", 0x98),
        new McDevice("DX", Bit, 16, "DX", 0xA2),
        new McDevice("DY", Bit, 16, "DY", 0xA3),
        new McDevice("Z", Word, 10, "Z*", 0xCC),
        new McDevice("R", Word, 10, "R*", 0xAF),
        new McDevice("ZR", Word, 16, "ZR", 0xB0),
    };

    /// <summary>软元件地址解析器。位号按十六进制解析（例如 <c>D100.F</c>，P2-D10）。</summary>
    public static readonly PlcAddressParser<McDevice, McAddress> AddressParser = new PlcAddressParser<McDevice, McAddress>(
        Devices,
        (device, number, bit, text) => new McAddress(device, number, bit, text),
        bitIndexRadix: 16);
}
