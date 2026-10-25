using System.Globalization;
using System.Text;
using Junevy.Communication.Secs;

namespace Junevy.Communication.DebugAssistant.Protocols.Secs;

/// <summary>
/// 解析 <c>SecsItem.ToSml()</c> 的输出（计划第 7.6 节，DA-D13）。只支持库能输出的格式：<c>L</c>、<c>A</c>、<c>B</c>、
/// <c>BOOLEAN</c>、<c>I1</c>–<c>I8</c>、<c>U1</c>–<c>U8</c>、<c>F4</c>、<c>F8</c>；<c>J</c> 与 <c>C2</c> 与库一样不支持，报错。
/// 解析的结果再经 <c>ToSml()</c> 输出，逐字等于原文本（性质测试）。
/// </summary>
/// <remarks>
/// 嵌套深度限制为 64（与库的解码上限 P51-D5 一致），避免恶意输入把递归压爆栈。
/// 数值按不变区域性解析；浮点接受库用 <c>G9</c> / <c>G17</c> 写出的形式，以及 <c>NaN</c>、<c>Infinity</c>、<c>∞</c>。
/// </remarks>
public static class SmlParser
{
    private const int MaxDepth = 64;

    private static readonly HashSet<string> ValueFormats =
    [
        "A", "B", "BOOLEAN", "I1", "I2", "I4", "I8", "U1", "U2", "U4", "U8", "F4", "F8",
    ];

    /// <summary>解析一个数据项（正文的一项，顶层不缩进）。文本为空、格式错误或有多余内容时抛出 <see cref="SmlParseException"/>。</summary>
    /// <param name="text">SML 文本。</param>
    /// <returns>解析出的数据项。</returns>
    public static SecsItem ParseItem(string text)
    {
        var reader = new SmlReader(text);
        reader.SkipWhitespace();
        if (reader.IsAtEnd)
        {
            throw reader.Fail("没有数据项");
        }

        SecsItem item = ReadItem(reader, 0);
        reader.SkipWhitespace();
        if (!reader.IsAtEnd)
        {
            throw reader.Fail("数据项之后还有多余的内容");
        }

        return item;
    }

    private static SecsItem ReadItem(SmlReader reader, int depth)
    {
        if (depth == MaxDepth)
        {
            throw reader.Fail($"嵌套超过 {MaxDepth} 层");
        }

        reader.Expect('<', "数据项应以 '<' 开始");
        int tagPosition = reader.Position;
        string tag = ReadTag(reader);
        if (tag == "L")
        {
            return ReadList(reader, depth);
        }

        if (ValueFormats.Contains(tag))
        {
            return ReadValue(reader, tag);
        }

        if (tag is "J" or "C2")
        {
            throw reader.FailAt(tagPosition, $"格式 {tag} 不支持（本库不支持 J 与 C2）");
        }

        throw reader.FailAt(tagPosition, $"不认识的格式 '{tag}'");
    }

    private static string ReadTag(SmlReader reader)
    {
        int start = reader.Position;
        string tag = reader.ReadWhile(char.IsAsciiLetterOrDigit);
        if (tag.Length == 0)
        {
            throw reader.FailAt(start, "'<' 之后应为格式名称");
        }

        return tag;
    }

    // 列表：<L [n] 子项… >。计数与子项数不符时报在闭合的 '>' 上。
    private static SecsItem ReadList(SmlReader reader, int depth)
    {
        reader.SkipWhitespace();
        reader.Expect('[', "列表的长度应写成 [n]");
        int declared = ReadCount(reader);
        reader.Expect(']', "列表的长度缺少 ']'");

        var children = new List<SecsItem>();
        while (true)
        {
            reader.SkipWhitespace();
            if (reader.Peek == '>')
            {
                if (children.Count != declared)
                {
                    throw reader.Fail($"列表声明 {declared} 个子项，实际是 {children.Count} 个");
                }

                reader.Advance();
                return SecsItem.L([.. children]);
            }

            if (reader.IsAtEnd)
            {
                throw reader.Fail("列表没有结束，缺少 '>'");
            }

            children.Add(ReadItem(reader, depth + 1));
        }
    }

    private static int ReadCount(SmlReader reader)
    {
        int start = reader.Position;
        string digits = reader.ReadWhile(char.IsAsciiDigit);
        if (!int.TryParse(digits, NumberStyles.None, CultureInfo.InvariantCulture, out int count))
        {
            throw reader.FailAt(start, "列表的长度应为十进制整数");
        }

        return count;
    }

    private static SecsItem ReadValue(SmlReader reader, string format)
    {
        if (format == "A")
        {
            return ReadAscii(reader);
        }

        List<SmlToken> tokens = ReadTokens(reader);
        return format switch
        {
            "B" => SecsItem.B(Each(tokens, token => ParseByte(reader, token))),
            "BOOLEAN" => SecsItem.Boolean(Each(tokens, token => ParseBoolean(reader, token))),
            "I1" => SecsItem.I1(Each(tokens, token => (sbyte)Integer(reader, token, format, sbyte.MinValue, sbyte.MaxValue))),
            "I2" => SecsItem.I2(Each(tokens, token => (short)Integer(reader, token, format, short.MinValue, short.MaxValue))),
            "I4" => SecsItem.I4(Each(tokens, token => (int)Integer(reader, token, format, int.MinValue, int.MaxValue))),
            "I8" => SecsItem.I8(Each(tokens, token => Integer(reader, token, format, long.MinValue, long.MaxValue))),
            "U1" => SecsItem.U1(Each(tokens, token => (byte)Integer(reader, token, format, byte.MinValue, byte.MaxValue))),
            "U2" => SecsItem.U2(Each(tokens, token => (ushort)Integer(reader, token, format, ushort.MinValue, ushort.MaxValue))),
            "U4" => SecsItem.U4(Each(tokens, token => (uint)Integer(reader, token, format, uint.MinValue, uint.MaxValue))),
            "U8" => SecsItem.U8(Each(tokens, token => ParseUnsigned(reader, token))),
            "F4" => SecsItem.F4(Each(tokens, token => ParseSingle(reader, token))),
            _ => SecsItem.F8(Each(tokens, token => ParseDouble(reader, token))),
        };
    }

    // 字符串：引号内的 " 与 \ 用反斜杠转义，\xHH 表示一个字节；其余字符必须在 0x20–0x7E 之间。
    private static SecsItem ReadAscii(SmlReader reader)
    {
        reader.SkipWhitespace();
        if (reader.TryConsume('>'))
        {
            return SecsItem.A(string.Empty);
        }

        reader.Expect('"', "字符串应以 '\"' 开始");
        var text = new StringBuilder();
        while (true)
        {
            if (reader.IsAtEnd)
            {
                throw reader.Fail("字符串没有结束的引号");
            }

            int position = reader.Position;
            char current = reader.Advance();
            if (current == '"')
            {
                break;
            }

            if (current == '\\')
            {
                text.Append(ReadEscape(reader, position));
            }
            else if (current is < ' ' or > '~')
            {
                throw reader.FailAt(position, $"字符 U+{(int)current:X4} 应写成 \\xHH");
            }
            else
            {
                text.Append(current);
            }
        }

        reader.SkipWhitespace();
        reader.Expect('>', "字符串之后应为 '>'");
        return SecsItem.A(text.ToString());
    }

    private static char ReadEscape(SmlReader reader, int backslashPosition)
    {
        if (reader.IsAtEnd)
        {
            throw reader.FailAt(backslashPosition, "反斜杠之后缺少转义字符");
        }

        char kind = reader.Advance();
        switch (kind)
        {
            case '"':
            case '\\':
                return kind;
            case 'x':
                return ReadHexEscape(reader, backslashPosition);
            default:
                throw reader.FailAt(backslashPosition, $"不认识的转义 '\\{kind}'");
        }
    }

    private static char ReadHexEscape(SmlReader reader, int backslashPosition)
    {
        if (reader.Remaining < 2 || !IsHexDigit(reader.Peek) || !IsHexDigit(reader.PeekAt(1)))
        {
            throw reader.FailAt(backslashPosition, "\\x 之后应为两个十六进制数字");
        }

        string digits = reader.ReadWhile(_ => true, 2);
        return (char)byte.Parse(digits, NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture);
    }

    // 数值与 B、BOOLEAN 的值用空格分隔，直到闭合的 '>'；每个记号记下起点，报错时用它定位。
    private static List<SmlToken> ReadTokens(SmlReader reader)
    {
        var tokens = new List<SmlToken>();
        while (true)
        {
            reader.SkipWhitespace();
            if (reader.TryConsume('>'))
            {
                return tokens;
            }

            if (reader.IsAtEnd)
            {
                throw reader.Fail("数据项没有结束，缺少 '>'");
            }

            int start = reader.Position;
            tokens.Add(new SmlToken(reader.ReadWhile(current => !char.IsWhiteSpace(current) && current != '>'), start));
        }
    }

    private static T[] Each<T>(List<SmlToken> tokens, Func<SmlToken, T> parse) => tokens.Select(parse).ToArray();

    private static byte ParseByte(SmlReader reader, SmlToken token)
    {
        bool hasPrefix = token.Text.StartsWith("0x", StringComparison.OrdinalIgnoreCase);
        string digits = hasPrefix ? token.Text[2..] : string.Empty;
        if (!hasPrefix || digits.Length is < 1 or > 2 || !digits.All(IsHexDigit))
        {
            throw reader.FailAt(token.Position, $"B 的值 '{token.Text}' 应为 0x 开头的一到两位十六进制数");
        }

        return byte.Parse(digits, NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture);
    }

    private static bool ParseBoolean(SmlReader reader, SmlToken token) => token.Text switch
    {
        "T" => true,
        "F" => false,
        _ => throw reader.FailAt(token.Position, $"BOOLEAN 的值 '{token.Text}' 应为 T 或 F"),
    };

    private static long Integer(SmlReader reader, SmlToken token, string format, long min, long max)
    {
        if (!long.TryParse(token.Text, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out long value))
        {
            throw reader.FailAt(token.Position, $"{format} 的值 '{token.Text}' 不是十进制整数");
        }

        if (value < min || value > max)
        {
            throw reader.FailAt(token.Position, $"{format} 的值 {token.Text} 超出 {min} 到 {max}");
        }

        return value;
    }

    private static ulong ParseUnsigned(SmlReader reader, SmlToken token)
    {
        if (!ulong.TryParse(token.Text, NumberStyles.None, CultureInfo.InvariantCulture, out ulong value))
        {
            throw reader.FailAt(token.Position, $"U8 的值 '{token.Text}' 不是十进制无符号整数");
        }

        return value;
    }

    private static float ParseSingle(SmlReader reader, SmlToken token)
    {
        if (TrySpecial(token.Text, out double special))
        {
            return (float)special;
        }

        return float.TryParse(token.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out float value)
            ? value
            : throw reader.FailAt(token.Position, $"F4 的值 '{token.Text}' 不是数值");
    }

    private static double ParseDouble(SmlReader reader, SmlToken token)
    {
        if (TrySpecial(token.Text, out double special))
        {
            return special;
        }

        return double.TryParse(token.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out double value)
            ? value
            : throw reader.FailAt(token.Position, $"F8 的值 '{token.Text}' 不是数值");
    }

    // 库在不同框架上写出的非有限值：NaN、正无穷与负无穷（"∞" 是不变区域性在部分框架上的写法）。
    private static bool TrySpecial(string text, out double value)
    {
        switch (text)
        {
            case "NaN":
                value = double.NaN;
                return true;
            case "Infinity":
            case "+Infinity":
            case "∞":
            case "+∞":
                value = double.PositiveInfinity;
                return true;
            case "-Infinity":
            case "-∞":
                value = double.NegativeInfinity;
                return true;
            default:
                value = 0;
                return false;
        }
    }

    private static bool IsHexDigit(char value) => char.IsAsciiHexDigit(value);

    /// <summary>一个以空白或 '&gt;' 分隔的记号，记下它在文本中的起点。</summary>
    private readonly record struct SmlToken(string Text, int Position);

    /// <summary>逐字符读取文本，并能算出任一位置的行号与列号。</summary>
    private sealed class SmlReader(string text)
    {
        private int _position;

        public int Position => _position;

        public bool IsAtEnd => _position >= text.Length;

        public int Remaining => text.Length - _position;

        public char Peek => IsAtEnd ? '\0' : text[_position];

        public char PeekAt(int offset) => _position + offset < text.Length ? text[_position + offset] : '\0';

        public char Advance() => text[_position++];

        public bool TryConsume(char expected)
        {
            if (Peek != expected || IsAtEnd)
            {
                return false;
            }

            _position++;
            return true;
        }

        public void Expect(char expected, string message)
        {
            if (!TryConsume(expected))
            {
                throw Fail(message);
            }
        }

        public void SkipWhitespace()
        {
            while (!IsAtEnd && char.IsWhiteSpace(text[_position]))
            {
                _position++;
            }
        }

        public string ReadWhile(Func<char, bool> accept, int limit = int.MaxValue)
        {
            int start = _position;
            while (!IsAtEnd && _position - start < limit && accept(text[_position]))
            {
                _position++;
            }

            return text[start.._position];
        }

        public SmlParseException Fail(string message) => FailAt(_position, message);

        public SmlParseException FailAt(int index, string message)
        {
            int line = 1;
            int lineStart = 0;
            for (int i = 0; i < index && i < text.Length; i++)
            {
                if (text[i] == '\n')
                {
                    line++;
                    lineStart = i + 1;
                }
            }

            return new SmlParseException(message, line, index - lineStart + 1);
        }
    }
}
