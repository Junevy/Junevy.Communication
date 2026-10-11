using Junevy.Communication.Core.Buffers;

namespace Junevy.Communication.Plc.Pipeline;

/// <summary>
/// 字与字节中的位（P2-D10、P4-D6）：从规范化布局的字节读取或修改位。按字访问时点 p 位于第 <c>p / 16</c> 个字的第 <c>p % 16</c> 位，
/// 位 0 是字值的最低位，字值按 <see cref="WordOrder"/> 从两个字节解出；按字节访问时点 p 位于第 <c>p / 8</c> 个字节的第 <c>p % 8</c> 位，
/// 位 0 是字节的最低位。
/// </summary>
internal static class PlcBitPacking
{
    /// <summary>
    /// 覆盖从 <paramref name="bitStart"/> 起的 <paramref name="count"/> 个点所需的字数。
    /// </summary>
    /// <param name="bitStart">起始位号（0–15）。</param>
    /// <param name="count">点数。</param>
    /// <returns>字数。</returns>
    public static int WordCount(int bitStart, int count) => CellCount(bitStart, count, PlcLayout.BitsPerWord);

    /// <summary>
    /// 覆盖从 <paramref name="bitStart"/> 起的 <paramref name="count"/> 个点所需的字节数（字节软元件，P4-D6）。
    /// </summary>
    /// <param name="bitStart">起始位号（0–7）。</param>
    /// <param name="count">点数。</param>
    /// <returns>字节数。</returns>
    public static int ByteCount(int bitStart, int count) => CellCount(bitStart, count, PlcLayout.BitsPerByte);

    /// <summary>
    /// 从字中取出位。
    /// </summary>
    /// <param name="words">规范化布局的字节，至少覆盖所需的字。</param>
    /// <param name="order">字序。</param>
    /// <param name="bitStart">起始位号（0–15）。</param>
    /// <param name="count">点数。</param>
    /// <returns>每点的值。</returns>
    public static bool[] Extract(byte[] words, WordOrder order, int bitStart, int count)
        => ExtractBits(bitStart, count, PlcLayout.BitsPerWord,
                       word => ByteTransform.ToUInt16(words.AsSpan(word * PlcLayout.WordBytes, PlcLayout.WordBytes), order));

    /// <summary>
    /// 从字节中取出位（字节软元件，P4-D6）。
    /// </summary>
    /// <param name="bytes">设备内存顺序的字节，至少覆盖所需的字节。</param>
    /// <param name="bitStart">起始位号（0–7）。</param>
    /// <param name="count">点数。</param>
    /// <returns>每点的值。</returns>
    public static bool[] ExtractFromBytes(byte[] bytes, int bitStart, int count)
        => ExtractBits(bitStart, count, PlcLayout.BitsPerByte, index => bytes[index]);

    /// <summary>
    /// 把位写入字（原地修改），其余位保持不变。
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

    // 取位循环只有一份：单元号与单元内位号由单元的位数决定，单元的值由调用方给出（字要先按字序解出，字节本身就是值）。
    private static bool[] ExtractBits(int bitStart, int count, int bitsPerCell, Func<int, int> cellValue)
    {
        var bits = new bool[count];
        for (int i = 0; i < count; i++)
        {
            int point = bitStart + i;
            bits[i] = ((cellValue(point / bitsPerCell) >> (point % bitsPerCell)) & 1) != 0;
        }

        return bits;
    }

    private static int CellCount(int bitStart, int count, int bitsPerCell)
        => (int)(((long)bitStart + count + bitsPerCell - 1) / bitsPerCell);

    private static int WordOffset(int point) => point / PlcLayout.BitsPerWord * PlcLayout.WordBytes;
}
