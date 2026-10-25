using System.Globalization;
using Junevy.Communication.DebugAssistant.Protocols.Plc;
using Junevy.Communication.Melsec;
using Junevy.Communication.Plc;

namespace Junevy.Communication.DebugAssistant.Protocols.Melsec;

/// <summary>
/// MC 的帧类型、编码与传输的组合（计划第 7.3 节）：TCP 与 UDP 只能用 3E 或 4E；串口只能用 4C，
/// 其中 ASCII 为格式 4，二进制为格式 5。编码对三种帧都可以选。
/// </summary>
public static class McCombination
{
    /// <summary>传输允许的帧类型。</summary>
    public static IReadOnlyList<McFrameType> FramesFor(PlcTransport transport) => transport == PlcTransport.Serial
        ? [McFrameType.Frame4C]
        : [McFrameType.Frame3E, McFrameType.Frame4E];

    /// <summary>组合不合法时的原因；合法时返回 null。</summary>
    public static string? Problem(PlcTransport transport, McFrameType frame) => FramesFor(transport).Contains(frame)
        ? null
        : transport == PlcTransport.Serial
            ? "串口只能使用 4C 帧（ASCII 为格式 4，二进制为格式 5）。"
            : "TCP 与 UDP 只能使用 3E 或 4E 帧。";

    /// <summary>组合的名称；不合法时返回 null。</summary>
    public static string? Name(PlcTransport transport, McFrameType frame, McEncoding encoding)
    {
        if (Problem(transport, frame) is not null)
        {
            return null;
        }

        string encodingName = encoding == McEncoding.Ascii ? "ASCII" : "二进制";
        return frame switch
        {
            McFrameType.Frame4C => $"4C 格式 {(encoding == McEncoding.Ascii ? 4 : 5)}（{encodingName}，串口）",
            _ => $"{(frame == McFrameType.Frame3E ? "3E" : "4E")}（{encodingName}，{(transport == PlcTransport.Udp ? "UDP" : "TCP")}）",
        };
    }
}

/// <summary>
/// 一个软元件的速查项（计划第 7.3 节）：符号、单位、进制，以及两种帧中的代码。名称不用 <c>McDevice</c>，
/// 那是库的软元件定义类型（<c>McAddress.Device</c>）。
/// </summary>
public sealed record McDeviceEntry(string Symbol, PlcAddressUnit Unit, int Radix, string AsciiCode, string BinaryCode);

/// <summary>
/// MC 的软元件速查表（28 项）。表由助手维护（库的软元件表是 internal，摩擦 F-3）；测试逐项用 <c>ParseAddress</c> 核对单位与进制。
/// 十六进制软元件（X、Y、B、W、SB、SW、DX、DY、ZR）的编号按十六进制书写。
/// </summary>
public static class McDevices
{
    public static IReadOnlyList<McDeviceEntry> All { get; } =
    [
        new("SM", PlcAddressUnit.Bit, 10, "SM", "0x91"),
        new("SD", PlcAddressUnit.Word, 10, "SD", "0xA9"),
        new("X", PlcAddressUnit.Bit, 16, "X*", "0x9C"),
        new("Y", PlcAddressUnit.Bit, 16, "Y*", "0x9D"),
        new("M", PlcAddressUnit.Bit, 10, "M*", "0x90"),
        new("L", PlcAddressUnit.Bit, 10, "L*", "0x92"),
        new("F", PlcAddressUnit.Bit, 10, "F*", "0x93"),
        new("V", PlcAddressUnit.Bit, 10, "V*", "0x94"),
        new("B", PlcAddressUnit.Bit, 16, "B*", "0xA0"),
        new("D", PlcAddressUnit.Word, 10, "D*", "0xA8"),
        new("W", PlcAddressUnit.Word, 16, "W*", "0xB4"),
        new("TS", PlcAddressUnit.Bit, 10, "TS", "0xC1"),
        new("TC", PlcAddressUnit.Bit, 10, "TC", "0xC0"),
        new("TN", PlcAddressUnit.Word, 10, "TN", "0xC2"),
        new("SS", PlcAddressUnit.Bit, 10, "SS", "0xC7"),
        new("SC", PlcAddressUnit.Bit, 10, "SC", "0xC6"),
        new("SN", PlcAddressUnit.Word, 10, "SN", "0xC8"),
        new("CS", PlcAddressUnit.Bit, 10, "CS", "0xC4"),
        new("CC", PlcAddressUnit.Bit, 10, "CC", "0xC3"),
        new("CN", PlcAddressUnit.Word, 10, "CN", "0xC5"),
        new("SB", PlcAddressUnit.Bit, 16, "SB", "0xA1"),
        new("SW", PlcAddressUnit.Word, 16, "SW", "0xB5"),
        new("S", PlcAddressUnit.Bit, 10, "S*", "0x98"),
        new("DX", PlcAddressUnit.Bit, 16, "DX", "0xA2"),
        new("DY", PlcAddressUnit.Bit, 16, "DY", "0xA3"),
        new("Z", PlcAddressUnit.Word, 10, "Z*", "0xCC"),
        new("R", PlcAddressUnit.Word, 10, "R*", "0xAF"),
        new("ZR", PlcAddressUnit.Word, 16, "ZR", "0xB0"),
    ];

    /// <summary>速查项的示例地址（编号为 0）。</summary>
    public static string SampleAddress(McDeviceEntry device) => device.Symbol + "0";
}

/// <summary>MC 的监视定时器（计划第 7.3 节）：单位为 250 毫秒。</summary>
public static class McTimers
{
    public const int UnitMilliseconds = 250;

    /// <summary>折算的毫秒数，以及大于或等于请求超时时的警告。</summary>
    public static (long Milliseconds, string? Warning) Describe(int units, int requestTimeoutMilliseconds)
    {
        long milliseconds = (long)units * UnitMilliseconds;
        string? warning = milliseconds >= requestTimeoutMilliseconds
            ? $"监视定时器 {milliseconds} 毫秒不小于请求超时 {requestTimeoutMilliseconds} 毫秒：客户端会在 PLC 返回之前超时。"
            : null;
        return (milliseconds, warning);
    }
}

/// <summary>MC 访问路径的十六进制输入（计划第 7.3 节）：网络号、PC 号、模块 I/O 号、模块站号、站号、本站号。</summary>
public static class McAccessPath
{
    /// <summary>解析一个字节的十六进制（可带 0x 前缀），范围 0 到 0xFF。</summary>
    public static byte ParseByte(string text) => (byte)Parse(text, 0xFF);

    /// <summary>解析一个字的十六进制（可带 0x 前缀），范围 0 到 0xFFFF。</summary>
    public static ushort ParseWord(string text) => (ushort)Parse(text, 0xFFFF);

    /// <summary>用 <c>uint</c> 解析，超出 32 位的文字解析失败，不会回绕成合法值。</summary>
    private static uint Parse(string text, uint max)
    {
        string digits = text.Trim();
        if (digits.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
        {
            digits = digits[2..];
        }

        if (digits.Length == 0 || !uint.TryParse(digits, NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out uint value) || value > max)
        {
            throw new FormatException($"“{text}”不是 0 到 {max:X} 之间的十六进制数。");
        }

        return value;
    }
}
