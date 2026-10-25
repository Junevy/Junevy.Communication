namespace Junevy.Communication.DebugAssistant.Protocols.Fins;

/// <summary>一个 FINS 区域的速查项（计划第 7.5 节）：符号，以及字区与位区的区域代码。</summary>
public sealed record FinsAreaEntry(string Symbol, byte WordAreaCode, byte BitAreaCode)
{
    public string WordAreaCodeText => $"0x{WordAreaCode:X2}";

    public string BitAreaCodeText => $"0x{BitAreaCode:X2}";
}

/// <summary>
/// FINS 的区域速查表（18 项，计划第 7.5 节）：CIO、W、H、A、D，以及 EM 的 E0_–EC_（字区代码 0xA0 + 组号，位区代码 0x20 + 组号）。
/// 表由助手维护（库的区域表是 internal，摩擦 F-3）；测试逐项用 <c>ParseAddress</c> 核对两个区域代码。
/// </summary>
public static class FinsAreas
{
    public static IReadOnlyList<FinsAreaEntry> All { get; } =
    [
        new("CIO", 0xB0, 0x30),
        new("W", 0xB1, 0x31),
        new("H", 0xB2, 0x32),
        new("A", 0xB3, 0x33),
        new("D", 0x82, 0x02),
        new("E0_", 0xA0, 0x20),
        new("E1_", 0xA1, 0x21),
        new("E2_", 0xA2, 0x22),
        new("E3_", 0xA3, 0x23),
        new("E4_", 0xA4, 0x24),
        new("E5_", 0xA5, 0x25),
        new("E6_", 0xA6, 0x26),
        new("E7_", 0xA7, 0x27),
        new("E8_", 0xA8, 0x28),
        new("E9_", 0xA9, 0x29),
        new("EA_", 0xAA, 0x2A),
        new("EB_", 0xAB, 0x2B),
        new("EC_", 0xAC, 0x2C),
    ];
}

/// <summary>FINS 节点与网络参数的范围（计划第 7.5 节，README "Options"）。</summary>
public static class FinsNodes
{
    /// <summary>客户端节点：0 表示由 CPU 分配，1 到 254 为固定节点；255 是广播地址，被拒绝。超出范围时返回原因，合法时返回 null。</summary>
    public static string? ClientNodeProblem(int clientNode) => clientNode is >= 0 and <= 254
        ? null
        : clientNode == 255
            ? "客户端节点 255 是 FINS 的广播地址，不能使用。"
            : "客户端节点必须在 0 到 254 之间（0 表示由 CPU 分配）。";

    /// <summary>网络号与单元号的范围是 0 到 255；超出时返回原因，合法时返回 null。</summary>
    public static string? ByteProblem(string name, int value) => value is >= 0 and <= 255
        ? null
        : $"{name} 必须在 0 到 255 之间。";
}
