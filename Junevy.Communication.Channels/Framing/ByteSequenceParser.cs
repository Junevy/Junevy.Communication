using System.Text;

namespace Junevy.Communication.Channels.Framing;

/// <summary>
/// 解析配置中的字节序列文本（分隔符、起始符、结束符、心跳内容）。
/// 规则：以 <c>hex:</c> 开头时，其后为十六进制字节，可用空格或 <c>-</c> 分隔（例如 <c>hex:0D 0A</c>、<c>hex:0D-0A</c>、<c>hex:0D0A</c>）；
/// 否则按文本解析，支持转义 <c>\r \n \t \0 \\ \xHH</c>，其余字符按 UTF-8 编码。
/// </summary>
public static class ByteSequenceParser
{
    private const string HexPrefix = "hex:";

    /// <summary>
    /// 解析字节序列文本。
    /// </summary>
    /// <param name="text">要解析的文本；不能为 null。</param>
    /// <returns>解析得到的字节。</returns>
    /// <exception cref="ArgumentNullException"><paramref name="text"/> 为 null。</exception>
    /// <exception cref="FormatException">文本为空、十六进制非法、转义序列非法。</exception>
    public static byte[] Parse(string text)
    {
        if (text == null)
            throw new ArgumentNullException(nameof(text));

        if (text.Length == 0)
            throw new FormatException("A byte sequence must not be empty.");

        if (text.StartsWith(HexPrefix, StringComparison.OrdinalIgnoreCase))
            return ParseHex(text.Substring(HexPrefix.Length));

        return ParseText(text);
    }

    private static byte[] ParseHex(string digits)
    {
        var builder = new StringBuilder(digits.Length);
        foreach (char c in digits)
        {
            if (c != ' ' && c != '-')
                builder.Append(c);
        }

        if (builder.Length == 0)
            throw new FormatException("A hex byte sequence must contain at least one byte.");

        if (builder.Length % 2 != 0)
            throw new FormatException("A hex byte sequence must contain an even number of hex digits.");

        var result = new byte[builder.Length / 2];
        for (int i = 0; i < result.Length; i++)
        {
            int high = HexValue(builder[2 * i]);
            int low = HexValue(builder[2 * i + 1]);
            if (high < 0 || low < 0)
                throw new FormatException($"Invalid hexadecimal digit in byte sequence '{digits}'.");

            result[i] = (byte)((high << 4) | low);
        }

        return result;
    }

    private static byte[] ParseText(string text)
    {
        var bytes = new List<byte>(text.Length);
        var pending = new StringBuilder();
        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];
            if (c != '\\')
            {
                pending.Append(c);
                continue;
            }

            AppendEncodedText(pending, bytes);
            if (i + 1 >= text.Length)
                throw new FormatException($"Trailing backslash in byte sequence '{text}'.");

            char escape = text[++i];
            switch (escape)
            {
                case 'r':
                    bytes.Add(0x0D);
                    break;
                case 'n':
                    bytes.Add(0x0A);
                    break;
                case 't':
                    bytes.Add(0x09);
                    break;
                case '0':
                    bytes.Add(0x00);
                    break;
                case '\\':
                    bytes.Add(0x5C);
                    break;
                case 'x':
                    if (i + 2 >= text.Length)
                        throw new FormatException($"Incomplete \\x escape in byte sequence '{text}'.");

                    int high = HexValue(text[i + 1]);
                    int low = HexValue(text[i + 2]);
                    if (high < 0 || low < 0)
                        throw new FormatException($"Invalid \\x escape in byte sequence '{text}'.");

                    bytes.Add((byte)((high << 4) | low));
                    i += 2;
                    break;
                default:
                    throw new FormatException($"Unsupported escape sequence '\\{escape}' in byte sequence '{text}'.");
            }
        }

        AppendEncodedText(pending, bytes);
        return bytes.ToArray();
    }

    private static void AppendEncodedText(StringBuilder pending, List<byte> bytes)
    {
        if (pending.Length == 0)
            return;

        bytes.AddRange(Encoding.UTF8.GetBytes(pending.ToString()));
        pending.Clear();
    }

    private static int HexValue(char c)
    {
        if (c >= '0' && c <= '9')
            return c - '0';
        if (c >= 'A' && c <= 'F')
            return c - 'A' + 10;
        if (c >= 'a' && c <= 'f')
            return c - 'a' + 10;
        return -1;
    }
}
