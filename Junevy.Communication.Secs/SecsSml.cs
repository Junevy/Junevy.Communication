using System.Globalization;
using System.Text;

namespace Junevy.Communication.Secs;

/// <summary>
/// SML 文本的渲染（SEMI E5 附录，计划第 17.3 节）。只输出、不解析：库不做 SML 解析，文本只用于日志与调试。
/// </summary>
/// <remarks>
/// 换行一律用 <c>\n</c>，不用 <see cref="Environment.NewLine"/>：同一段文本在两个目标框架与两台机器上必须逐字相同。
/// 单精度用 <c>G9</c>、双精度用 <c>G17</c>——.NET Framework 上 <c>"R"</c> 不保证往返（官方文档对 Single 给 <c>G9</c>、
/// 对 Double 给 <c>G17</c>），而这两个格式在 net472 与 net8.0 上给出同样的文本。
/// </remarks>
internal static class SecsSml
{
    private const string Newline = "\n";
    private const string IndentUnit = "  ";
    private const string HexPrefix = "0x";
    private const string TrueToken = "T";
    private const string FalseToken = "F";
    private const string MessageEnd = ".";

    // 正文的顶层项不缩进，第 17.3 节的样例里 <L [2]> 就在第一列；缩进只出现在列表的子项上。
    private const int TopLevelDepth = 0;

    // 各自平台上可往返的有效数字位数；net472 上 "R" 不保证往返，见类型注释。
    private const string SingleFormat = "G9";
    private const string DoubleFormat = "G17";

    private const char Quote = '"';
    private const char Backslash = '\\';
    private const char FirstPrintable = ' ';
    private const char LastPrintable = '~';

    /// <summary>
    /// 整条消息：首行是 <c>S{Stream}F{Function}</c>（带 W 位时后跟空格与 <c>W</c>），正文每项缩进一级，末行是 <c>.</c>。
    /// </summary>
    internal static string Message(SecsMessage message)
    {
        var text = new StringBuilder();
        text.Append('S').Append(Number(message.Stream))
            .Append('F').Append(Number(message.Function));
        if (message.ReplyExpected)
            text.Append(" W");

        text.Append(Newline);
        if (message.Body is not null)
            Append(text, message.Body, TopLevelDepth);
        text.Append(MessageEnd);

        return text.ToString();
    }

    /// <summary>单个数据项的文本片段，顶层不缩进，且不以换行结尾（它可能嵌进更大的文本里）。</summary>
    internal static string Item(SecsItem item)
    {
        var text = new StringBuilder();
        Append(text, item, TopLevelDepth);

        // Append 收尾时一定写了一个换行，片段不该把它带出去。
        text.Length -= Newline.Length;
        return text.ToString();
    }

    // 列表占三步：标签行（带上层缩进）、每个子项缩进一级、闭合的 > 回到本项的缩进。
    private static void Append(StringBuilder text, SecsItem item, int depth)
    {
        Indent(text, depth);
        if (item.Format != SecsFormat.List)
        {
            text.Append(Tag(item.Format));
            AppendValues(text, item);
            text.Append('>').Append(Newline);
            return;
        }

        text.Append("<L [").Append(Number(item.Count)).Append(']');
        if (item.Count == 0)
        {
            // 空列表没有子项可缩进，闭合的 > 与标签同行（SML 的写法就是 <L [0]>）。
            text.Append('>').Append(Newline);
            return;
        }

        text.Append(Newline);
        foreach (SecsItem child in item.Items)
            Append(text, child, depth + 1);
        Indent(text, depth);
        text.Append('>').Append(Newline);
    }

    // 三种格式的元素不是十进制数，单独写；其余都是数字，只差用哪个访问器。
    private static void AppendValues(StringBuilder text, SecsItem item)
    {
        if (item.Format == SecsFormat.Ascii)
        {
            text.Append(' ').Append(Quote).Append(EscapeAscii(item.GetString())).Append(Quote);
            return;
        }

        if (item.Format == SecsFormat.Binary)
        {
            foreach (byte value in item.GetBinary())
                text.Append(' ').Append(HexPrefix).Append(value.ToString("X2", CultureInfo.InvariantCulture));
            return;
        }

        if (item.Format == SecsFormat.Boolean)
        {
            foreach (bool value in item.GetBooleans())
                text.Append(' ').Append(value ? TrueToken : FalseToken);
            return;
        }

        AppendNumbers(text, item);
    }

    private static void AppendNumbers(StringBuilder text, SecsItem item)
    {
        switch (item.Format)
        {
            case SecsFormat.I1: AppendEach(text, item.GetI1()); break;
            case SecsFormat.I2: AppendEach(text, item.GetI2()); break;
            case SecsFormat.I4: AppendEach(text, item.GetI4()); break;
            case SecsFormat.I8: AppendEach(text, item.GetI8()); break;
            case SecsFormat.U1: AppendEach(text, item.GetU1()); break;
            case SecsFormat.U2: AppendEach(text, item.GetU2()); break;
            case SecsFormat.U4: AppendEach(text, item.GetU4()); break;
            case SecsFormat.U8: AppendEach(text, item.GetU8()); break;
            case SecsFormat.F4: AppendEach(text, item.GetF4(), SingleFormat); break;
            case SecsFormat.F8: AppendEach(text, item.GetF8(), DoubleFormat); break;

            // 走到这里说明 SecsFormat 加了成员而这里没跟上：标签已经写出去了，不该再补内容。
            default: throw new InvalidOperationException($"The SML text does not define the values of {item.Format}.");
        }
    }

    private static void AppendEach<T>(StringBuilder text, IReadOnlyList<T> values, string? format = null)
        where T : IFormattable
    {
        foreach (T value in values)
            text.Append(' ').Append(value.ToString(format, CultureInfo.InvariantCulture));
    }

    private static void Indent(StringBuilder text, int depth)
    {
        for (int level = 0; level < depth; level++)
            text.Append(IndentUnit);
    }

    private static string Number(int value) => value.ToString(CultureInfo.InvariantCulture);

    private static string Tag(SecsFormat format) => format switch
    {
        SecsFormat.List => "<L",
        SecsFormat.Binary => "<B",
        SecsFormat.Boolean => "<BOOLEAN",
        SecsFormat.Ascii => "<A",
        _ => "<" + format.ToString(),
    };

    // 可打印 ASCII 原样输出，引号与反斜杠转义，其余写成 \xHH。
    private static string EscapeAscii(string value)
    {
        var text = new StringBuilder(value.Length);
        foreach (char character in value)
        {
            if (character == Quote)
                text.Append(Backslash).Append(Quote);
            else if (character == Backslash)
                text.Append(Backslash).Append(Backslash);
            else if (character >= FirstPrintable && character <= LastPrintable)
                text.Append(character);
            else
                text.Append("\\x").Append(((byte)character).ToString("X2", CultureInfo.InvariantCulture));
        }

        return text.ToString();
    }
}