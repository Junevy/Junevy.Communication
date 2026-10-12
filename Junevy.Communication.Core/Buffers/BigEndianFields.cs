using System.Buffers;
using System.Buffers.Binary;

namespace Junevy.Communication.Core.Buffers;

/// <summary>
/// 报文头的大端字段读写：多字节字段一律高位在前，适合把协议报文的固定头逐字段拼出或逐字段拆开。
/// 写入到 <see cref="IBufferWriter{T}"/>；读取从切片头部取出并把切片前移。
/// </summary>
/// <remarks>
/// 读取在字节不足时返回 <see langword="false"/>，既不移动 <c>data</c>，<c>value</c> 也取默认值——结构上的长度不足由解码器判定并走结果，不抛异常。
/// 参数错误（<c>output</c> 为 null、<see cref="WriteUInt24"/> 超出 0–0xFFFFFF、<see cref="TryTakeBytes"/> 的长度为负）抛
/// <see cref="ArgumentNullException"/> 或 <see cref="ArgumentOutOfRangeException"/>。
/// </remarks>
public static class BigEndianFields
{
    /// <summary>1 字节字段的宽度。</summary>
    public const int UInt8Bytes = 1;

    /// <summary>2 字节字段的宽度。</summary>
    public const int UInt16Bytes = 2;

    /// <summary>3 字节字段的宽度。</summary>
    public const int UInt24Bytes = 3;

    /// <summary>4 字节字段的宽度。</summary>
    public const int UInt32Bytes = 4;

    private const int UInt24Min = 0;
    private const int UInt24Max = 0xFFFFFF;

    /// <summary>写入 1 字节。</summary>
    /// <param name="output">目标缓冲。</param>
    /// <param name="value">字节值。</param>
    /// <exception cref="ArgumentNullException"><paramref name="output"/> 为 null。</exception>
    public static void WriteUInt8(IBufferWriter<byte> output, byte value)
    {
        if (output == null)
            throw new ArgumentNullException(nameof(output));

        output.GetSpan(UInt8Bytes)[0] = value;
        output.Advance(UInt8Bytes);
    }

    /// <summary>写入 2 字节（高位在前）。</summary>
    /// <param name="output">目标缓冲。</param>
    /// <param name="value">数值。</param>
    /// <exception cref="ArgumentNullException"><paramref name="output"/> 为 null。</exception>
    public static void WriteUInt16(IBufferWriter<byte> output, ushort value)
    {
        if (output == null)
            throw new ArgumentNullException(nameof(output));

        BinaryPrimitives.WriteUInt16BigEndian(output.GetSpan(UInt16Bytes), value);
        output.Advance(UInt16Bytes);
    }

    /// <summary>写入 3 字节（高位在前）。</summary>
    /// <param name="output">目标缓冲。</param>
    /// <param name="value">数值，取值范围 0–0xFFFFFF。</param>
    /// <exception cref="ArgumentNullException"><paramref name="output"/> 为 null。</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="value"/> 不在 0–0xFFFFFF 之间。</exception>
    public static void WriteUInt24(IBufferWriter<byte> output, int value)
    {
        if (output == null)
            throw new ArgumentNullException(nameof(output));
        if (value < UInt24Min || value > UInt24Max)
            throw new ArgumentOutOfRangeException(nameof(value), value, $"The value must be between {UInt24Min} and 0x{UInt24Max:X}.");

        Span<byte> span = output.GetSpan(UInt24Bytes);
        span[0] = (byte)(value >> 16);
        span[1] = (byte)(value >> 8);
        span[2] = (byte)value;
        output.Advance(UInt24Bytes);
    }

    /// <summary>写入 4 字节（高位在前）。</summary>
    /// <param name="output">目标缓冲。</param>
    /// <param name="value">数值。</param>
    /// <exception cref="ArgumentNullException"><paramref name="output"/> 为 null。</exception>
    public static void WriteUInt32(IBufferWriter<byte> output, uint value)
    {
        if (output == null)
            throw new ArgumentNullException(nameof(output));

        BinaryPrimitives.WriteUInt32BigEndian(output.GetSpan(UInt32Bytes), value);
        output.Advance(UInt32Bytes);
    }

    /// <summary>原样写入一段字节。</summary>
    /// <param name="output">目标缓冲。</param>
    /// <param name="value">要写入的字节。</param>
    /// <exception cref="ArgumentNullException"><paramref name="output"/> 为 null。</exception>
    public static void WriteBytes(IBufferWriter<byte> output, ReadOnlySpan<byte> value)
    {
        if (output == null)
            throw new ArgumentNullException(nameof(output));

        value.CopyTo(output.GetSpan(value.Length));
        output.Advance(value.Length);
    }

    /// <summary>读取 1 字节并把切片前移。</summary>
    /// <param name="data">剩余字节。</param>
    /// <param name="value">读到的值；字节不足时为 0。</param>
    /// <returns>字节足够返回 <see langword="true"/>。</returns>
    public static bool TryTakeUInt8(ref ReadOnlySpan<byte> data, out byte value)
    {
        if (data.Length < UInt8Bytes)
        {
            value = 0;
            return false;
        }

        value = data[0];
        data = data.Slice(UInt8Bytes);
        return true;
    }

    /// <summary>读取 2 字节（高位在前）并把切片前移。</summary>
    /// <param name="data">剩余字节。</param>
    /// <param name="value">读到的值；字节不足时为 0。</param>
    /// <returns>字节足够返回 <see langword="true"/>。</returns>
    public static bool TryTakeUInt16(ref ReadOnlySpan<byte> data, out ushort value)
    {
        if (data.Length < UInt16Bytes)
        {
            value = 0;
            return false;
        }

        value = BinaryPrimitives.ReadUInt16BigEndian(data);
        data = data.Slice(UInt16Bytes);
        return true;
    }

    /// <summary>读取 3 字节（高位在前）并把切片前移。</summary>
    /// <param name="data">剩余字节。</param>
    /// <param name="value">读到的值；字节不足时为 0。</param>
    /// <returns>字节足够返回 <see langword="true"/>。</returns>
    public static bool TryTakeUInt24(ref ReadOnlySpan<byte> data, out int value)
    {
        if (data.Length < UInt24Bytes)
        {
            value = 0;
            return false;
        }

        value = data[0] << 16 | data[1] << 8 | data[2];
        data = data.Slice(UInt24Bytes);
        return true;
    }

    /// <summary>读取 4 字节（高位在前）并把切片前移。</summary>
    /// <param name="data">剩余字节。</param>
    /// <param name="value">读到的值；字节不足时为 0。</param>
    /// <returns>字节足够返回 <see langword="true"/>。</returns>
    public static bool TryTakeUInt32(ref ReadOnlySpan<byte> data, out uint value)
    {
        if (data.Length < UInt32Bytes)
        {
            value = 0;
            return false;
        }

        value = BinaryPrimitives.ReadUInt32BigEndian(data);
        data = data.Slice(UInt32Bytes);
        return true;
    }

    /// <summary>切出指定长度的字节并把切片前移。</summary>
    /// <param name="data">剩余字节。</param>
    /// <param name="length">要切出的长度；0 表示不消耗字节。</param>
    /// <param name="value">切出的片段；剩余字节不足时为空。</param>
    /// <returns>字节足够返回 <see langword="true"/>。</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="length"/> 为负数。</exception>
    public static bool TryTakeBytes(ref ReadOnlySpan<byte> data, int length, out ReadOnlySpan<byte> value)
    {
        if (length < 0)
            throw new ArgumentOutOfRangeException(nameof(length), length, "The length must not be negative.");
        if (data.Length < length)
        {
            value = ReadOnlySpan<byte>.Empty;
            return false;
        }

        value = data.Slice(0, length);
        data = data.Slice(length);
        return true;
    }
}