namespace Junevy.Communication.DebugAssistant.Simulators.Melsec;

/// <summary>模拟器认识的软元件（计划第 8.1 节 Melsec）：符号、二进制代码、是否为位软元件、编号是否为十六进制。</summary>
internal sealed record McDeviceInfo(string Symbol, byte BinaryCode, bool IsBit, bool IsHexadecimal);

/// <summary>
/// 模拟器的软元件表（与客户端的速查表分开维护，DA-D14）：D、W、TN 为字区，M、X、Y、B 为位区。
/// 每个软元件对应同名的存储区。
/// </summary>
internal static class McDeviceTable
{
    public static readonly IReadOnlyList<McDeviceInfo> All =
    [
        new("D", 0xA8, IsBit: false, IsHexadecimal: false),
        new("W", 0xB4, IsBit: false, IsHexadecimal: true),
        new("TN", 0xC2, IsBit: false, IsHexadecimal: false),
        new("M", 0x90, IsBit: true, IsHexadecimal: false),
        new("X", 0x9C, IsBit: true, IsHexadecimal: true),
        new("Y", 0x9D, IsBit: true, IsHexadecimal: true),
        new("B", 0xA0, IsBit: true, IsHexadecimal: true),
    ];

    public static McDeviceInfo? FindByCode(byte code) => All.FirstOrDefault(device => device.BinaryCode == code);

    /// <summary>按符号查找；ASCII 帧中一个字符的符号用 '*' 补齐，查找时去掉。</summary>
    public static McDeviceInfo? FindBySymbol(string symbol) =>
        All.FirstOrDefault(device => string.Equals(device.Symbol, symbol.Trim('*', ' '), StringComparison.OrdinalIgnoreCase));
}
