using System.Globalization;
using System.Text;
using Junevy.Communication.Channels.Framing;

namespace Junevy.Communication.DebugAssistant.Common;

/// <summary>
/// 把界面上输入的字节文本转换为字节。语法与库的 <see cref="ByteSequenceParser"/> 相同（计划 DA-D12）：
/// <c>hex:</c> 开头为十六进制；否则为文本，支持 <c>\r \n \t \0 \\ \xHH</c> 转义。
/// 文本部分的编码可选：UTF-8 直接交给库；ASCII 与 GBK 由这里按所选编码转换（转义规则与库一致）。
/// </summary>
public static class ByteInput
{
    private const string HexPrefix = "hex:";

    /// <summary>解析输入。格式错误时抛出 <see cref="FormatException"/>，消息用英文（与库一致）。</summary>
    public static byte[] Parse(string text, EncodingChoice encoding)
    {
        ArgumentNullException.ThrowIfNull(text);
        if (text.StartsWith(HexPrefix, StringComparison.OrdinalIgnoreCase) || encoding == EncodingChoice.Utf8)
        {
            return ByteSequenceParser.Parse(text);
        }

        return ParseWithEncoding(text, encoding.ToEncoding());
    }

    private static byte[] ParseWithEncoding(string text, Encoding encoding)
    {
        if (text.Length == 0)
        {
            throw new FormatException("A byte sequence must not be empty.");
        }

        var bytes = new List<byte>(text.Length);
        var pending = new StringBuilder();
        for (int i = 0; i < text.Length; i++)
        {
            if (text[i] != '\\')
            {
                pending.Append(text[i]);
                continue;
            }

            Flush(pending, bytes, encoding);
            if (i + 1 >= text.Length)
            {
                throw new FormatException($"Trailing backslash in byte sequence '{text}'.");
            }

            i = AppendEscape(text, i + 1, bytes);
        }

        Flush(pending, bytes, encoding);
        return bytes.ToArray();
    }

    /// <summary>处理 <paramref name="index"/> 处的转义字符，返回最后一个被消耗的字符的下标。</summary>
    private static int AppendEscape(string text, int index, List<byte> bytes)
    {
        switch (text[index])
        {
            case 'r':
                bytes.Add(0x0D);
                return index;
            case 'n':
                bytes.Add(0x0A);
                return index;
            case 't':
                bytes.Add(0x09);
                return index;
            case '0':
                bytes.Add(0x00);
                return index;
            case '\\':
                bytes.Add((byte)'\\');
                return index;
            case 'x':
                if (index + 2 >= text.Length
                    || !byte.TryParse(text.AsSpan(index + 1, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out byte value))
                {
                    throw new FormatException($"Invalid \\x escape in byte sequence '{text}'.");
                }

                bytes.Add(value);
                return index + 2;
            default:
                throw new FormatException($"Unknown escape '\\{text[index]}' in byte sequence '{text}'.");
        }
    }

    private static void Flush(StringBuilder pending, List<byte> bytes, Encoding encoding)
    {
        if (pending.Length == 0)
        {
            return;
        }

        bytes.AddRange(encoding.GetBytes(pending.ToString()));
        pending.Clear();
    }
}
