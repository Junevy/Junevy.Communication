using Junevy.Communication.Core.Buffers;

namespace Junevy.Communication.Plc.Pipeline;

/// <summary>
/// 字中的位（P2-D10）：从规范化布局的字节读取或修改位。点 p 位于第 <c>p / 16</c> 个字的第 <c>p % 16</c> 位，位 0 是字值的最低位；
/// 字值按 <see cref="WordOrder"/> 从两个字节解出。
/// </summary>
internal static class PlcBitPacking
{
    /// <summary>
    /// 覆盖从 <paramref name="bitStart"/> 起的 <paramref name="count"/> 个点所需的字数。
    /// </summary>
    /// <param name="bitStart">起始位号（0–15）。</param>
    /// <param name="count">点数。</param>
    /// <returns>字数。</returns>
    public static int WordCount(int bitStart, int count)
        => (int)(((long)bitStart + count + PlcLayout.BitsPerWord - 1) / PlcLayout.BitsPerWord);

    /// <summary>
    /// 从字节中取出位。
    /// </summary>
    /// <param name="words">规范化布局的字节，至少覆盖所需的字。</param>
    /// <param name="order">字序。</param>
    /// <param name="bitStart">起始位号（0–15）。</param>
    /// <param name="count">点数。</param>
    /// <returns>每点的值。</returns>
    public static bool[] Extract(byte[] words, WordOrder order, int bitStart, int count)
    {
        var bits = new bool[count];
        for (int i = 0; i < count; i++)
        {
            int point = bitStart + i;
            ushort word = ByteTransform.ToUInt16(words.AsSpan(WordOffset(point), PlcLayout.WordBytes), order);
            bits[i] = ((word >> (point % PlcLayout.BitsPerWord)) & 1) != 0;
        }

        return bits;
    }

    /// <summary>
    /// 把位写入字节（原地修改），其余位保持不变。
    /// </summary>
    /// <param name="words">规范化布局的字节，至少覆盖所需的字。</param>
    /// <param name="order">字序。</param>
    /// <param name="bitStart">起始位号（0–15）。</param>
    /// <param name="values">每点的新值。</param>
    public static void Insert(byte[] words, WordOrder order, int bitStart, ReadOnlySpan<bool> values)
    {
        for (int i = 0; i < values.Length; i++)
        {
            int point = bitStart + i;
            Span<byte> cell = words.AsSpan(WordOffset(point), PlcLayout.WordBytes);
            ushort word = ByteTransform.ToUInt16(cell, order);
            int mask = 1 << (point % PlcLayout.BitsPerWord);
            word = values[i] ? (ushort)(word | mask) : (ushort)(word & ~mask);
            ByteTransform.WriteUInt16(cell, word, order);
        }
    }

    private static int WordOffset(int point) => point / PlcLayout.BitsPerWord * PlcLayout.WordBytes;
}
