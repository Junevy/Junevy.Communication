using System.Buffers.Binary;
using System.Runtime.InteropServices;

namespace Junevy.Communication.Core.Buffers;

/// <summary>
/// 数值与设备字节序列的互转。数值先写成大端字节，再按 <see cref="WordOrder"/> 切成 16 位字重排；读取与写入共用同一个重排函数
/// （四种排列都是对合，再排一次即还原）。读取只使用源的前 N 个字节；写入只改写目标的前 N 个字节，其余字节保持不变。
/// </summary>
/// <remarks>
/// 长度不足时抛出 <see cref="ArgumentException"/>；未定义的字序抛出 <see cref="ArgumentOutOfRangeException"/>。
/// </remarks>
public static class ByteTransform
{
    private const int WordSize = 2;
    private const int Size16 = sizeof(short);
    private const int Size32 = sizeof(int);
    private const int Size64 = sizeof(long);

    // ————— 读取：设备字节 → 数值 —————

    /// <summary>按字序读取 16 位有符号整数。</summary>
    /// <param name="source">设备字节，至少 2 个字节。</param>
    /// <param name="order">字序。</param>
    /// <returns>数值。</returns>
    /// <exception cref="ArgumentException"><paramref name="source"/> 长度不足。</exception>
    public static short ToInt16(ReadOnlySpan<byte> source, WordOrder order)
        => Read(source, Size16, order, BinaryPrimitives.ReadInt16BigEndian);

    /// <summary>按字序读取 16 位无符号整数。</summary>
    /// <param name="source">设备字节，至少 2 个字节。</param>
    /// <param name="order">字序。</param>
    /// <returns>数值。</returns>
    /// <exception cref="ArgumentException"><paramref name="source"/> 长度不足。</exception>
    public static ushort ToUInt16(ReadOnlySpan<byte> source, WordOrder order)
        => Read(source, Size16, order, BinaryPrimitives.ReadUInt16BigEndian);

    /// <summary>按字序读取 32 位有符号整数。</summary>
    /// <param name="source">设备字节，至少 4 个字节。</param>
    /// <param name="order">字序。</param>
    /// <returns>数值。</returns>
    /// <exception cref="ArgumentException"><paramref name="source"/> 长度不足。</exception>
    public static int ToInt32(ReadOnlySpan<byte> source, WordOrder order)
        => Read(source, Size32, order, BinaryPrimitives.ReadInt32BigEndian);

    /// <summary>按字序读取 32 位无符号整数。</summary>
    /// <param name="source">设备字节，至少 4 个字节。</param>
    /// <param name="order">字序。</param>
    /// <returns>数值。</returns>
    /// <exception cref="ArgumentException"><paramref name="source"/> 长度不足。</exception>
    public static uint ToUInt32(ReadOnlySpan<byte> source, WordOrder order)
        => Read(source, Size32, order, BinaryPrimitives.ReadUInt32BigEndian);

    /// <summary>按字序读取 64 位有符号整数。</summary>
    /// <param name="source">设备字节，至少 8 个字节。</param>
    /// <param name="order">字序。</param>
    /// <returns>数值。</returns>
    /// <exception cref="ArgumentException"><paramref name="source"/> 长度不足。</exception>
    public static long ToInt64(ReadOnlySpan<byte> source, WordOrder order)
        => Read(source, Size64, order, BinaryPrimitives.ReadInt64BigEndian);

    /// <summary>按字序读取 64 位无符号整数。</summary>
    /// <param name="source">设备字节，至少 8 个字节。</param>
    /// <param name="order">字序。</param>
    /// <returns>数值。</returns>
    /// <exception cref="ArgumentException"><paramref name="source"/> 长度不足。</exception>
    public static ulong ToUInt64(ReadOnlySpan<byte> source, WordOrder order)
        => Read(source, Size64, order, BinaryPrimitives.ReadUInt64BigEndian);

    /// <summary>按字序读取单精度浮点数（IEEE 754 位模式）。</summary>
    /// <param name="source">设备字节，至少 4 个字节。</param>
    /// <param name="order">字序。</param>
    /// <returns>数值。</returns>
    /// <exception cref="ArgumentException"><paramref name="source"/> 长度不足。</exception>
    public static float ToSingle(ReadOnlySpan<byte> source, WordOrder order)
        => Read(source, Size32, order, ReadSingleBigEndian);

    /// <summary>按字序读取双精度浮点数（IEEE 754 位模式）。</summary>
    /// <param name="source">设备字节，至少 8 个字节。</param>
    /// <param name="order">字序。</param>
    /// <returns>数值。</returns>
    /// <exception cref="ArgumentException"><paramref name="source"/> 长度不足。</exception>
    public static double ToDouble(ReadOnlySpan<byte> source, WordOrder order)
        => Read(source, Size64, order, ReadDoubleBigEndian);

    // ————— 写入：数值 → 设备字节 —————

    /// <summary>按字序写入 16 位有符号整数，只改写 <paramref name="destination"/> 的前 2 个字节。</summary>
    /// <param name="destination">目标缓冲区，至少 2 个字节。</param>
    /// <param name="value">数值。</param>
    /// <param name="order">字序。</param>
    /// <exception cref="ArgumentException"><paramref name="destination"/> 长度不足。</exception>
    public static void WriteInt16(Span<byte> destination, short value, WordOrder order)
        => Write(destination, Size16, value, order, BinaryPrimitives.WriteInt16BigEndian);

    /// <summary>按字序写入 16 位无符号整数，只改写 <paramref name="destination"/> 的前 2 个字节。</summary>
    /// <param name="destination">目标缓冲区，至少 2 个字节。</param>
    /// <param name="value">数值。</param>
    /// <param name="order">字序。</param>
    /// <exception cref="ArgumentException"><paramref name="destination"/> 长度不足。</exception>
    public static void WriteUInt16(Span<byte> destination, ushort value, WordOrder order)
        => Write(destination, Size16, value, order, BinaryPrimitives.WriteUInt16BigEndian);

    /// <summary>按字序写入 32 位有符号整数，只改写 <paramref name="destination"/> 的前 4 个字节。</summary>
    /// <param name="destination">目标缓冲区，至少 4 个字节。</param>
    /// <param name="value">数值。</param>
    /// <param name="order">字序。</param>
    /// <exception cref="ArgumentException"><paramref name="destination"/> 长度不足。</exception>
    public static void WriteInt32(Span<byte> destination, int value, WordOrder order)
        => Write(destination, Size32, value, order, BinaryPrimitives.WriteInt32BigEndian);

    /// <summary>按字序写入 32 位无符号整数，只改写 <paramref name="destination"/> 的前 4 个字节。</summary>
    /// <param name="destination">目标缓冲区，至少 4 个字节。</param>
    /// <param name="value">数值。</param>
    /// <param name="order">字序。</param>
    /// <exception cref="ArgumentException"><paramref name="destination"/> 长度不足。</exception>
    public static void WriteUInt32(Span<byte> destination, uint value, WordOrder order)
        => Write(destination, Size32, value, order, BinaryPrimitives.WriteUInt32BigEndian);

    /// <summary>按字序写入 64 位有符号整数，只改写 <paramref name="destination"/> 的前 8 个字节。</summary>
    /// <param name="destination">目标缓冲区，至少 8 个字节。</param>
    /// <param name="value">数值。</param>
    /// <param name="order">字序。</param>
    /// <exception cref="ArgumentException"><paramref name="destination"/> 长度不足。</exception>
    public static void WriteInt64(Span<byte> destination, long value, WordOrder order)
        => Write(destination, Size64, value, order, BinaryPrimitives.WriteInt64BigEndian);

    /// <summary>按字序写入 64 位无符号整数，只改写 <paramref name="destination"/> 的前 8 个字节。</summary>
    /// <param name="destination">目标缓冲区，至少 8 个字节。</param>
    /// <param name="value">数值。</param>
    /// <param name="order">字序。</param>
    /// <exception cref="ArgumentException"><paramref name="destination"/> 长度不足。</exception>
    public static void WriteUInt64(Span<byte> destination, ulong value, WordOrder order)
        => Write(destination, Size64, value, order, BinaryPrimitives.WriteUInt64BigEndian);

    /// <summary>按字序写入单精度浮点数，只改写 <paramref name="destination"/> 的前 4 个字节。</summary>
    /// <param name="destination">目标缓冲区，至少 4 个字节。</param>
    /// <param name="value">数值。</param>
    /// <param name="order">字序。</param>
    /// <exception cref="ArgumentException"><paramref name="destination"/> 长度不足。</exception>
    public static void WriteSingle(Span<byte> destination, float value, WordOrder order)
        => Write(destination, Size32, value, order, WriteSingleBigEndian);

    /// <summary>按字序写入双精度浮点数，只改写 <paramref name="destination"/> 的前 8 个字节。</summary>
    /// <param name="destination">目标缓冲区，至少 8 个字节。</param>
    /// <param name="value">数值。</param>
    /// <param name="order">字序。</param>
    /// <exception cref="ArgumentException"><paramref name="destination"/> 长度不足。</exception>
    public static void WriteDouble(Span<byte> destination, double value, WordOrder order)
        => Write(destination, Size64, value, order, WriteDoubleBigEndian);

    // ————— 读写共用的骨架：长度校验 → 重排 → 大端解码或编码 —————

    private delegate T BigEndianReader<T>(ReadOnlySpan<byte> bigEndian);

    private delegate void BigEndianWriter<T>(Span<byte> bigEndian, T value);

    private static T Read<T>(ReadOnlySpan<byte> source, int size, WordOrder order, BigEndianReader<T> reader)
    {
        RequireLength(source.Length, size, nameof(source));

        Span<byte> bigEndian = stackalloc byte[size];
        Reorder(source, bigEndian, order);
        return reader(bigEndian);
    }

    private static void Write<T>(Span<byte> destination, int size, T value, WordOrder order, BigEndianWriter<T> writer)
    {
        RequireLength(destination.Length, size, nameof(destination));

        Span<byte> bigEndian = stackalloc byte[size];
        writer(bigEndian, value);
        Reorder(bigEndian, destination.Slice(0, size), order);
    }

    // 读取（设备 → 大端）与写入（大端 → 设备）共用的唯一重排函数；映射是对合，因此两个方向使用同一个映射。
    private static void Reorder(ReadOnlySpan<byte> source, Span<byte> destination, WordOrder order)
    {
        ValidateOrder(order);
        for (int index = 0; index < destination.Length; index++)
            destination[index] = source[SourceIndex(index, destination.Length, order)];
    }

    // 大端位置 index 对应的源位置：字序决定字是否倒排，字节序决定字内两个字节是否互换。
    private static int SourceIndex(int index, int length, WordOrder order)
    {
        int word = index / WordSize;
        int byteInWord = index % WordSize;
        int lastWord = length / WordSize - 1;

        int sourceWord = order is WordOrder.CDAB or WordOrder.DCBA ? lastWord - word : word;
        int sourceByte = order is WordOrder.BADC or WordOrder.DCBA ? WordSize - 1 - byteInWord : byteInWord;
        return sourceWord * WordSize + sourceByte;
    }

    private static void ValidateOrder(WordOrder order)
    {
        if (order is not (WordOrder.ABCD or WordOrder.BADC or WordOrder.CDAB or WordOrder.DCBA))
            throw new ArgumentOutOfRangeException(nameof(order), order, "The word order is not defined.");
    }

    private static void RequireLength(int actual, int required, string paramName)
    {
        if (actual < required)
            throw new ArgumentException($"The buffer must contain at least {required} bytes for this value type.", paramName);
    }

    // ————— 浮点数与位模式互转 —————

    private static float ReadSingleBigEndian(ReadOnlySpan<byte> bigEndian)
        => new SingleView { Bits = BinaryPrimitives.ReadUInt32BigEndian(bigEndian) }.Value;

    private static void WriteSingleBigEndian(Span<byte> bigEndian, float value)
        => BinaryPrimitives.WriteUInt32BigEndian(bigEndian, new SingleView { Value = value }.Bits);

    private static double ReadDoubleBigEndian(ReadOnlySpan<byte> bigEndian)
        => new DoubleView { Bits = BinaryPrimitives.ReadUInt64BigEndian(bigEndian) }.Value;

    private static void WriteDoubleBigEndian(Span<byte> bigEndian, double value)
        => BinaryPrimitives.WriteUInt64BigEndian(bigEndian, new DoubleView { Value = value }.Bits);

    // 同一块内存的两种视图：浮点数与其位模式共享本机字节序，因此不涉及大端 / 小端转换（net472 没有 BitConverter.SingleToInt32Bits）。
    [StructLayout(LayoutKind.Explicit)]
    private struct SingleView
    {
        [FieldOffset(0)]
        public float Value;

        [FieldOffset(0)]
        public uint Bits;
    }

    [StructLayout(LayoutKind.Explicit)]
    private struct DoubleView
    {
        [FieldOffset(0)]
        public double Value;

        [FieldOffset(0)]
        public ulong Bits;
    }
}
