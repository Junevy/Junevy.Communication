using System.Buffers;
using System.Buffers.Binary;

namespace Junevy.Communication.Omron.Protocol;

/// <summary>
/// FINS 的大端字段读写：第 17.2 节的长度、命令、错误码、字地址与数量都是高位在前，所以只需要一种字节序。
/// 写入 <see cref="IBufferWriter{T}"/>，读取从切片头部取出并把切片前移（与西门子侧的 <c>S7Fields</c> 同职责，见摩擦记录 F8）。
/// </summary>
internal static class FinsFields
{
    /// <summary>1 字节字段的宽度。</summary>
    public const int UInt8Bytes = 1;

    /// <summary>2 字节字段的宽度（命令码、字地址、数量、结束码）。</summary>
    public const int UInt16Bytes = 2;

    /// <summary>4 字节字段的宽度（FINS/TCP 的长度、命令与错误码，以及节点号）。</summary>
    public const int UInt32Bytes = 4;

    /// <summary>写入 1 字节。</summary>
    /// <param name="buffer">目标缓冲。</param>
    /// <param name="value">字节值。</param>
    public static void WriteUInt8(IBufferWriter<byte> buffer, byte value)
    {
        buffer.GetSpan(UInt8Bytes)[0] = value;
        buffer.Advance(UInt8Bytes);
    }

    /// <summary>写入 2 字节（高位在前）。</summary>
    /// <param name="buffer">目标缓冲。</param>
    /// <param name="value">数值。</param>
    public static void WriteUInt16(IBufferWriter<byte> buffer, ushort value)
    {
        BinaryPrimitives.WriteUInt16BigEndian(buffer.GetSpan(UInt16Bytes), value);
        buffer.Advance(UInt16Bytes);
    }

    /// <summary>写入 4 字节（高位在前）。</summary>
    /// <param name="buffer">目标缓冲。</param>
    /// <param name="value">数值。</param>
    public static void WriteUInt32(IBufferWriter<byte> buffer, uint value)
    {
        BinaryPrimitives.WriteUInt32BigEndian(buffer.GetSpan(UInt32Bytes), value);
        buffer.Advance(UInt32Bytes);
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

        value = BinaryPrimitives.ReadUInt16BigEndian(data);
        data = data.Slice(UInt16Bytes);
        return true;
    }

    /// <summary>读取 4 字节（高位在前）并把切片前移。</summary>
    /// <param name="data">剩余字节。</param>
    /// <param name="value">读到的值；字节不足时为 0。</param>
    /// <returns>字节足够返回 true。</returns>
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
}