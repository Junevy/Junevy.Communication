namespace Junevy.Communication.DebugAssistant.Logging;

/// <summary>
/// 一帧报文。<see cref="Hex"/> 是库的 <c>HexFormatter</c> 输出（"AA-BB-…"）；超过 256 字节时被截断，
/// 此时 <see cref="Truncated"/> 为 true，<see cref="Length"/> 仍是完整长度（计划 DA-D5）。
/// </summary>
public sealed record FrameEntry(
    DateTimeOffset Time,
    FrameDirection Direction,
    int Length,
    string Hex,
    bool Truncated,
    string? Endpoint)
    : SessionEntry(Time)
{
    public override string Kind => "报文";

    public override string Summary
    {
        get
        {
            string arrow = Direction == FrameDirection.Tx ? "发送" : "接收";
            string endpoint = Endpoint is null ? string.Empty : (Direction == FrameDirection.Tx ? " 到 " : " 来自 ") + Endpoint;
            string truncated = Truncated ? "（已截断，共 " + Length + " 字节）" : string.Empty;
            return $"{arrow} {Length} 字节{endpoint}{truncated}";
        }
    }

    public override string HexColumn => Hex;

    /// <summary>被截断的报文没有完整的字节，ASCII 列显示说明而不是部分内容。</summary>
    public override string AsciiColumn => Truncated ? "（已截断）" : ToAscii(Hex);

    /// <summary>把 "AA-42-7F" 形式的十六进制还原为 ASCII，不可打印的字节显示为 '.'。无法解析时返回空串。</summary>
    private static string ToAscii(string hex)
    {
        var chars = new List<char>();
        foreach (string token in hex.Split('-', StringSplitOptions.RemoveEmptyEntries))
        {
            if (!byte.TryParse(token, System.Globalization.NumberStyles.HexNumber, null, out byte value))
            {
                return string.Empty;
            }

            chars.Add(value is >= 0x20 and < 0x7F ? (char)value : '.');
        }

        return new string(chars.ToArray());
    }
}

/// <summary>报文的方向。</summary>
public enum FrameDirection
{
    Tx,
    Rx,
}
