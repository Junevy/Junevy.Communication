using System.Globalization;
using System.Text;

namespace Junevy.Communication.Testing;

/// <summary>
/// 第 17 节黄金向量的文本与字节互转：向量按空格分隔的大写十六进制书写（例如 <c>"03 00 00 19"</c>）。
/// <see cref="Mutated"/> 与 <see cref="Truncated"/> 用来从合法向量派生违规帧，测试中不手写整段字节。
/// </summary>
public static class HexVector
{
    /// <summary>把向量文本转换为字节。</summary>
    /// <param name="vector">空格分隔的十六进制字节。</param>
    /// <returns>字节数组。</returns>
    public static byte[] ToBytes(string vector)
    {
        string digits = vector.Replace(" ", string.Empty);
        var bytes = new byte[digits.Length / 2];
        for (int i = 0; i < bytes.Length; i++)
            bytes[i] = Convert.ToByte(digits.Substring(i * 2, 2), 16);

        return bytes;
    }

    /// <summary>把字节转换回向量文本，让断言失败时能逐字节对照。</summary>
    /// <param name="frame">帧字节。</param>
    /// <returns>空格分隔的大写十六进制文本。</returns>
    public static string ToText(ReadOnlySpan<byte> frame)
    {
        var builder = new StringBuilder(frame.Length * 3);
        for (int i = 0; i < frame.Length; i++)
        {
            if (i > 0)
                builder.Append(' ');

            builder.Append(frame[i].ToString("X2", CultureInfo.InvariantCulture));
        }

        return builder.ToString();
    }

    /// <summary>取向量字节并替换其中一个位置。</summary>
    /// <param name="vector">空格分隔的十六进制字节。</param>
    /// <param name="index">要替换的位置（从 0 开始）。</param>
    /// <param name="value">替换后的字节。</param>
    /// <returns>替换后的帧。</returns>
    public static byte[] Mutated(string vector, int index, byte value)
    {
        byte[] frame = ToBytes(vector);
        frame[index] = value;
        return frame;
    }

    /// <summary>取向量的前若干字节，用来构造被截断的帧。</summary>
    /// <param name="vector">空格分隔的十六进制字节。</param>
    /// <param name="length">保留的字节数。</param>
    /// <returns>截断后的帧。</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="length"/> 不在向量长度之内。</exception>
    public static byte[] Truncated(string vector, int length)
    {
        byte[] frame = ToBytes(vector);
        if (length < 0 || length > frame.Length)
            throw new ArgumentOutOfRangeException(nameof(length), length, "The truncation length must stay within the vector.");

        var truncated = new byte[length];
        Array.Copy(frame, truncated, length);
        return truncated;
    }
}
