namespace Junevy.Communication.Modbus.Protocol;

/// <summary>
/// 线圈与离散输入的位打包（规范 V1.1b3 第 6.1 节）：每字节的最低位是该字节中的第一个点，末字节的高位补 0。
/// </summary>
internal static class ModbusBits
{
    private const int BitsPerByte = 8;

    /// <summary>容纳 <paramref name="points"/> 个点所需的字节数。</summary>
    /// <param name="points">点数。</param>
    /// <returns>字节数。</returns>
    public static int ByteCount(int points) => (points + BitsPerByte - 1) / BitsPerByte;

    /// <summary>把点值打包到 <paramref name="destination"/>，长度不足时抛出 <see cref="ArgumentException"/>。</summary>
    /// <param name="values">点值。</param>
    /// <param name="destination">目标，长度至少为 <see cref="ByteCount"/>。</param>
    public static void Pack(ReadOnlySpan<bool> values, Span<byte> destination)
    {
        Span<byte> packed = destination.Slice(0, ByteCount(values.Length));
        packed.Clear();
        for (int i = 0; i < values.Length; i++)
        {
            if (values[i])
                packed[i / BitsPerByte] |= (byte)(1 << (i % BitsPerByte));
        }
    }

    /// <summary>把打包的字节还原为点值。</summary>
    /// <param name="data">打包的字节，长度至少为 <see cref="ByteCount"/>。</param>
    /// <param name="count">点数。</param>
    /// <returns>点值。</returns>
    public static bool[] Unpack(ReadOnlySpan<byte> data, int count)
    {
        var values = new bool[count];
        for (int i = 0; i < count; i++)
            values[i] = ((data[i / BitsPerByte] >> (i % BitsPerByte)) & 1) != 0;

        return values;
    }
}
