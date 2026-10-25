using Junevy.Communication.Siemens;

namespace Junevy.Communication.DebugAssistant.Protocols.Siemens;

/// <summary>
/// S7 的远端与本地 TSAP（计划第 7.4 节，P4-D19）：远端 TSAP = (连接类型 &lt;&lt; 8) | (Rack &lt;&lt; 5) | 槽号，本地 TSAP 为 0x0100（Snap7 的约定）。
/// </summary>
public static class S7Tsap
{
    /// <summary>本地 TSAP。</summary>
    public const ushort Local = 0x0100;

    /// <summary>机架号与槽号超出范围时的原因；合法时返回 null。</summary>
    public static string? Problem(int rack, int slot) => rack is < 0 or > 7
        ? "Rack 必须在 0 到 7 之间。"
        : slot is < 0 or > 31
            ? "槽号必须在 0 到 31 之间。"
            : null;

    /// <summary>远端 TSAP。参数须先通过 <see cref="Problem"/> 的检查。</summary>
    public static ushort Remote(S7ConnectionType connectionType, int rack, int slot) =>
        (ushort)(((int)connectionType << 8) | (rack << 5) | slot);
}

/// <summary>一个 PDU 长度下的单包上限（计划第 7.4 节，README "Limits per Request"）。</summary>
public sealed record S7PduLimit(int ReadBytes, int WriteBytes, int ReadWords, int WriteWords);

/// <summary>S7 的单包上限：读取的头 18 字节，写入的头 28 字节（P4-D12）。</summary>
public static class S7PduLimits
{
    private const int ReadHeaderBytes = 18;
    private const int WriteHeaderBytes = 28;

    public static S7PduLimit For(int pduSize)
    {
        int readBytes = pduSize - ReadHeaderBytes;
        int writeBytes = pduSize - WriteHeaderBytes;
        return new S7PduLimit(readBytes, writeBytes, readBytes / 2, writeBytes / 2);
    }
}

/// <summary>S7 的字符串类型（计划第 7.4 节）：String 与 WString。</summary>
public enum S7StringKind
{
    /// <summary>String：最大长度 1 到 254，头部 2 字节（声明长度、实际长度各 1 字节）。</summary>
    String,

    /// <summary>WString：最大长度 1 到 16382，头部 4 字节（两个 16 位大端）。</summary>
    WString,
}

/// <summary>S7 字符串的长度范围与头部（README "Data Format and Strings"）。</summary>
public static class S7StringLimits
{
    /// <summary>最大长度超出范围时的原因；合法时返回 null。</summary>
    public static string? Problem(S7StringKind kind, int maxLength) => kind switch
    {
        S7StringKind.String when maxLength is < 1 or > 254 => "String 的最大长度必须在 1 到 254 之间。",
        S7StringKind.WString when maxLength is < 1 or > 16382 => "WString 的最大长度必须在 1 到 16382 之间。",
        _ => null,
    };

    /// <summary>头部字节数：String 为 2，WString 为 4。</summary>
    public static int HeaderBytes(S7StringKind kind) => kind == S7StringKind.String ? 2 : 4;

    /// <summary>从头部读出声明的最大长度与实际长度。</summary>
    public static (int Declared, int Actual) ReadHeader(S7StringKind kind, byte[] header) => kind == S7StringKind.String
        ? (header[0], header[1])
        : ((header[0] << 8) | header[1], (header[2] << 8) | header[3]);
}
