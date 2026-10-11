using System.Buffers;

namespace Junevy.Communication.Siemens.Protocol;

/// <summary>
/// S7 的大端字段读写。写入 <see cref="IBufferWriter{T}"/>，读取从切片头部取出并把切片前移；
/// S7 的多字节字段一律高位在前（计划第 17.1 节说明段），所以这里只需要一种字节序。
/// </summary>
internal static class S7Fields
{
    /// <summary>1 字节字段的宽度。</summary>
    public const int UInt8Bytes = 1;

    /// <summary>2 字节字段的宽度。</summary>
    public const int UInt16Bytes = 2;

    /// <summary>3 字节字段的宽度（S7 数据项的地址字段）。</summary>
    public const int UInt24Bytes = 3;

    /// <summary>写入 1 字节。</summary>
    /// <param name="buffer">目标缓冲。</param>
    /// <param name="value">字节值。</param>
    public static void WriteUInt8(IBufferWriter<byte> buffer, byte value)
    {
        Span<byte> span = buffer.GetSpan(UInt8Bytes);
        span[0] = value;
        buffer.Advance(UInt8Bytes);
    }

    /// <summary>写入 2 字节（高位在前）。</summary>
    /// <param name="buffer">目标缓冲。</param>
    /// <param name="value">数值。</param>
    public static void WriteUInt16(IBufferWriter<byte> buffer, ushort value)
    {
        Span<byte> span = buffer.GetSpan(UInt16Bytes);
        span[0] = (byte)(value >> 8);
        span[1] = (byte)value;
        buffer.Advance(UInt16Bytes);
    }

    /// <summary>
    /// 写入 3 字节（高位在前）。地址字段的上限由 <see cref="S7Device.MaxNumber"/> 保证：
    /// 字节号 × 8 + 位号 最大正好是 0xFFFFFF。
    /// </summary>
    /// <param name="buffer">目标缓冲。</param>
    /// <param name="value">数值。</param>
    public static void WriteUInt24(IBufferWriter<byte> buffer, int value)
    {
        Span<byte> span = buffer.GetSpan(UInt24Bytes);
        span[0] = (byte)(value >> 16);
        span[1] = (byte)(value >> 8);
        span[2] = (byte)value;
        buffer.Advance(UInt24Bytes);
    }

    /// <summary>原样写入一段字节。</summary>
    /// <param name="buffer">目标缓冲。</param>
    /// <param name="value">要写入的字节。</param>
    public static void WriteRaw(IBufferWriter<byte> buffer, ReadOnlySpan<byte> value)
    {
        value.CopyTo(buffer.GetSpan(value.Length));
        buffer.Advance(value.Length);
    }

    /// <summary>读取 1 字节并把切片前移。</summary>
    /// <param name="data">剩余字节。</param>
    /// <param name="value">读到的值；字节不足时为 0。</param>
    /// <returns>字节足够返回 true。</returns>
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
    /// <returns>字节足够返回 true。</returns>
    public static bool TryTakeUInt16(ref ReadOnlySpan<byte> data, out ushort value)
    {
        if (data.Length < UInt16Bytes)
        {
            value = 0;
            return false;
        }

        value = (ushort)(data[0] << 8 | data[1]);
        data = data.Slice(UInt16Bytes);
        return true;
    }

    /// <summary>读取 3 字节（高位在前）并把切片前移。</summary>
    /// <param name="data">剩余字节。</param>
    /// <param name="value">读到的值；字节不足时为 0。</param>
    /// <returns>字节足够返回 true。</returns>
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

    /// <summary>切出指定长度的字节并把切片前移。</summary>
    /// <param name="data">剩余字节。</param>
    /// <param name="length">要切出的长度。</param>
    /// <param name="value">切出的片段；剩余字节不足时为空。</param>
    /// <returns>字节足够返回 true。</returns>
    public static bool TryTakeRaw(ref ReadOnlySpan<byte> data, int length, out ReadOnlySpan<byte> value)
    {
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
