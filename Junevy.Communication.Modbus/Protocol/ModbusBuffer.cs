using System.Buffers;

namespace Junevy.Communication.Modbus.Protocol;

/// <summary>协议层写字节的小工具：只写入 <see cref="IBufferWriter{T}"/>，不依赖通道。</summary>
internal static class ModbusBuffer
{
    /// <summary>写入一个字节。</summary>
    /// <param name="output">目标。</param>
    /// <param name="value">字节。</param>
    public static void WriteByte(IBufferWriter<byte> output, byte value)
    {
        output.GetSpan(1)[0] = value;
        output.Advance(1);
    }

    /// <summary>写入一段字节。</summary>
    /// <param name="output">目标。</param>
    /// <param name="bytes">字节。</param>
    public static void Write(IBufferWriter<byte> output, ReadOnlySpan<byte> bytes)
    {
        bytes.CopyTo(output.GetSpan(bytes.Length));
        output.Advance(bytes.Length);
    }
}
