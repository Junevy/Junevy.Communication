using System.Text;

namespace Junevy.Communication.Core.Diagnostics;

/// <summary>
/// 十六进制格式化工具，供 TX/RX 日志使用。调用方应只在 Debug 级别启用时才生成字符串，避免无谓的格式化开销。
/// </summary>
public static class HexFormatter
{
    private const string Digits = "0123456789ABCDEF";

    /// <summary>
    /// 把字节序列格式化为 "AA-BB-CC"；超过 <paramref name="maxBytes"/> 时截断并追加 "...(+N bytes)"。
    /// </summary>
    /// <param name="data">要格式化的字节。</param>
    /// <param name="maxBytes">最多输出的字节数；不能为负。</param>
    /// <returns>十六进制文本；空输入返回空字符串。</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="maxBytes"/> 为负数。</exception>
    public static string ToHex(ReadOnlySpan<byte> data, int maxBytes = 256)
    {
        if (maxBytes < 0)
            throw new ArgumentOutOfRangeException(nameof(maxBytes), maxBytes, "MaxBytes must not be negative.");

        int shown = Math.Min(data.Length, maxBytes);
        var builder = new StringBuilder();
        for (int i = 0; i < shown; i++)
        {
            if (i > 0)
                builder.Append('-');

            byte value = data[i];
            builder.Append(Digits[value >> 4]).Append(Digits[value & 0x0F]);
        }

        if (data.Length > shown)
            builder.Append("...(+").Append(data.Length - shown).Append(" bytes)");

        return builder.ToString();
    }
}
