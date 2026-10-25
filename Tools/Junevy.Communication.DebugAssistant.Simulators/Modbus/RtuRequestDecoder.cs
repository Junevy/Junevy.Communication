using System.Buffers;
using Junevy.Communication.Channels;
using Junevy.Communication.Channels.Framing;

namespace Junevy.Communication.DebugAssistant.Simulators.Modbus;

/// <summary>
/// Modbus RTU 请求的分帧（计划第 8.2 节）：按功能码决定请求长度（01–06 为 8 字节，0F/10 由偏移 6 的字节数算出，
/// 16 为 10 字节，17 由偏移 10 的字节数算出）；CRC 不符，或功能码未知时丢弃 1 个字节并重新同步。
/// </summary>
internal sealed class RtuRequestDecoder : IFrameDecoder
{
    private const int FixedRequestLength = 8;
    private const int MaskWriteRequestLength = 10;
    private const int WriteMultipleCoilsOrRegistersOffset = 6;
    private const int ReadWriteMultipleOffset = 10;

    public bool TryDecode(ref ReadOnlySequence<byte> buffer, out ReadOnlySequence<byte> frame)
    {
        while (true)
        {
            byte[] data = buffer.ToArray();
            int length = RequestLength(data);
            if (length < 0)
            {
                // 功能码未知：这个字节不可能是帧的开头，跳过它重新同步。
                buffer = buffer.Slice(1);
                continue;
            }

            if (length == 0 || data.Length < length)
            {
                frame = default;
                return false;
            }

            if (!Crc16Modbus.Verify(data.AsSpan(0, length)))
            {
                buffer = buffer.Slice(1);
                continue;
            }

            frame = buffer.Slice(0, length);
            buffer = buffer.Slice(length);
            return true;
        }
    }

    /// <summary>请求的总长度；长度字段还未到达时返回 0，功能码未知时返回 -1。</summary>
    private static int RequestLength(byte[] data)
    {
        if (data.Length < 2)
        {
            return 0;
        }

        return data[1] switch
        {
            0x01 or 0x02 or 0x03 or 0x04 or 0x05 or 0x06 => FixedRequestLength,
            0x0F or 0x10 => data.Length <= WriteMultipleCoilsOrRegistersOffset ? 0 : WriteMultipleCoilsOrRegistersOffset + 1 + data[WriteMultipleCoilsOrRegistersOffset] + 2,
            0x16 => MaskWriteRequestLength,
            0x17 => data.Length <= ReadWriteMultipleOffset ? 0 : ReadWriteMultipleOffset + 1 + data[ReadWriteMultipleOffset] + 2,
            _ => -1,
        };
    }
}

/// <summary>Modbus RTU 的分帧工厂：请求用 <see cref="RtuRequestDecoder"/>，应答由引擎已经带好 CRC，原样发送。</summary>
internal sealed class RtuFrameCodecFactory : IFrameCodecFactory
{
    public IFrameDecoder CreateDecoder() => new RtuRequestDecoder();

    public IFrameEncoder CreateEncoder() => PassthroughFrameEncoder.Instance;
}
