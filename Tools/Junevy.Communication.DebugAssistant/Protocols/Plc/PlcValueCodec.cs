using System.Globalization;
using Junevy.Communication.Core.Diagnostics;
using Junevy.Communication.DebugAssistant.Common;

namespace Junevy.Communication.DebugAssistant.Protocols.Plc;

/// <summary>
/// 面板的值与文本之间的转换（计划第 7.1 节）。多个值用逗号分隔；整数可以写十进制，也可以写 <c>0x</c> 开头的位模式
/// （例如 <c>0xFFFF</c> 表示 Int16 的 -1）。字符串与字节类型整段作为一个值。
/// </summary>
internal static class PlcValueCodec
{
    /// <summary>把用户输入解析为该类型的值：标量类型为单个值，数组类型为逗号分隔的各项。格式错误或超出范围时抛出 <see cref="FormatException"/>。</summary>
    /// <param name="type">数据类型。</param>
    /// <param name="text">用户输入。</param>
    /// <param name="bytesEncoding">字节类型的文本编码（只用于 <c>Bytes</c> 与 <c>Words</c> 的文本形式）。</param>
    public static object Parse(PlcValueType type, string text, EncodingChoice bytesEncoding) => type switch
    {
        PlcValueType.Int16 => Items(text).Select(item => (short)SignedBits(item, 16)).ToArray(),
        PlcValueType.UInt16 => Items(text).Select(item => (ushort)UnsignedBits(item, 16)).ToArray(),
        PlcValueType.Int32 => Items(text).Select(item => (int)SignedBits(item, 32)).ToArray(),
        PlcValueType.UInt32 => Items(text).Select(item => (uint)UnsignedBits(item, 32)).ToArray(),
        PlcValueType.Int64 => Items(text).Select(item => SignedBits(item, 64)).ToArray(),
        PlcValueType.UInt64 => Items(text).Select(item => UnsignedBits(item, 64)).ToArray(),
        PlcValueType.Float => Items(text).Select(item => (float)Real(item)).ToArray(),
        PlcValueType.Double => Items(text).Select(Real).ToArray(),
        PlcValueType.String => text,
        PlcValueType.Bool => Bit(Single(text)),
        PlcValueType.Bits => Items(text).Select(Bit).ToArray(),
        PlcValueType.Byte => (byte)UnsignedBits(Single(text), 8),
        PlcValueType.Bytes or PlcValueType.Words => ByteInput.Parse(text, bytesEncoding),
        _ => throw new ArgumentOutOfRangeException(nameof(type), type, null),
    };

    /// <summary>把值格式化为显示文字：数组用逗号分隔，布尔值用 true、false（位数组用 1、0），字节用十六进制。</summary>
    public static string Format(object? value) => value switch
    {
        null => string.Empty,
        bool flag => flag ? "true" : "false",
        bool[] bits => string.Join(", ", bits.Select(bit => bit ? "1" : "0")),
        byte[] bytes => HexFormatter.ToHex(bytes),
        float number => number.ToString("R", CultureInfo.InvariantCulture),
        double number => number.ToString("R", CultureInfo.InvariantCulture),
        Array array => string.Join(", ", array.Cast<object>().Select(Format)),
        IFormattable formattable => formattable.ToString(null, CultureInfo.InvariantCulture),
        _ => value.ToString() ?? string.Empty,
    };

    /// <summary>逗号分隔的各项，去掉首尾空白；输入为空时抛出 <see cref="FormatException"/>。</summary>
    private static string[] Items(string text) => text.Trim().Length == 0
        ? throw new FormatException("请输入值。")
        : text.Split(',', StringSplitOptions.TrimEntries);

    /// <summary>标量类型只接受一个值。</summary>
    private static string Single(string text)
    {
        string[] items = Items(text);
        return items.Length == 1 ? items[0] : throw new FormatException("请输入一个值。");
    }

    /// <summary>有符号整数：十进制，或 <c>0x</c> 位模式（按位数做符号扩展）。</summary>
    private static long SignedBits(string item, int bits)
    {
        if (TryHex(item, bits, out ulong pattern))
        {
            return bits == 64 ? (long)pattern : (long)(pattern << (64 - bits)) >> (64 - bits);
        }

        return Decimal(item, bits);
    }

    /// <summary>无符号整数：十进制，或 <c>0x</c> 位模式。</summary>
    private static ulong UnsignedBits(string item, int bits)
    {
        if (TryHex(item, bits, out ulong pattern))
        {
            return pattern;
        }

        ulong max = bits == 64 ? ulong.MaxValue : (1UL << bits) - 1;
        return ulong.TryParse(item, NumberStyles.None, CultureInfo.InvariantCulture, out ulong value) && value <= max
            ? value
            : throw new FormatException($"“{item}”不是 {bits} 位无符号整数（0 到 {max}）。");
    }

    /// <summary>十进制的有符号整数；64 位由 <see cref="long"/> 本身检查范围。</summary>
    private static long Decimal(string item, int bits)
    {
        if (!long.TryParse(item, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out long value))
        {
            throw new FormatException($"“{item}”不是有效的整数。");
        }

        if (bits < 64 && (value < -(1L << (bits - 1)) || value >= 1L << (bits - 1)))
        {
            throw new FormatException($"“{item}”超出 {bits} 位有符号整数的范围。");
        }

        return value;
    }

    /// <summary><c>0x</c> 开头的位模式；位数超过类型宽度时抛出。不是位模式时返回 false。</summary>
    private static bool TryHex(string item, int bits, out ulong pattern)
    {
        pattern = 0;
        if (!item.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        string digits = item[2..];
        if (digits.Length == 0 || digits.Length > bits / 4 || !ulong.TryParse(digits, NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out pattern))
        {
            throw new FormatException($"“{item}”不是 {bits} 位以内的十六进制位模式。");
        }

        return true;
    }

    private static double Real(string item) =>
        double.TryParse(item, NumberStyles.Float, CultureInfo.InvariantCulture, out double value)
            ? value
            : throw new FormatException($"“{item}”不是有效的数字。");

    private static bool Bit(string item) => item.ToLowerInvariant() switch
    {
        "1" or "true" => true,
        "0" or "false" => false,
        _ => throw new FormatException($"“{item}”不是位值（1、0、true 或 false）。"),
    };
}
