using Junevy.Communication.Channels.Framing;

namespace Junevy.Communication.DebugAssistant.Common;

/// <summary>
/// 十六进制字节输入（计划 DA-D12）。与 <see cref="ByteInput"/> 不同，这里永远按十六进制解释、不做文本转义，
/// 因此 MQTT 的负载与 CorrelationData、OPC UA 的 ByteString 都用它。可以带或不带 <c>hex:</c> 前缀，字节之间可用空格或 <c>-</c> 分隔。
/// </summary>
public static class HexInput
{
    private const string Prefix = "hex:";

    /// <summary>解析十六进制字节。格式错误时抛出 <see cref="FormatException"/>，消息与库一致（英文）。</summary>
    public static byte[] Parse(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        string digits = text.Trim();
        if (digits.StartsWith(Prefix, StringComparison.OrdinalIgnoreCase))
        {
            digits = digits[Prefix.Length..].Trim();
        }

        return ByteSequenceParser.Parse(Prefix + digits);
    }
}
