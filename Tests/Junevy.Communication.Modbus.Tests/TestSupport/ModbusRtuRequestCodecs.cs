using System.Buffers;
using Junevy.Communication.Channels;
using Junevy.Communication.Channels.Framing;
using Junevy.Communication.Modbus.Framing;
using Junevy.Communication.Modbus.Protocol;
using Junevy.Communication.Testing;

namespace Junevy.Communication.Modbus.Tests;

/// <summary>
/// 设备一侧的分帧（测试专用）：RTU 请求按功能码定长（第 21.4 节的请求列）；MBAP 请求用生产的长度字段分帧。
/// 与客户端的应答分帧分开：客户端的解码器只认应答，这里只认请求。
/// </summary>
internal sealed class ModbusDeviceCodecs : IFrameCodecFactory
{
    private readonly ModbusFraming framing;

    private ModbusDeviceCodecs(ModbusFraming framing)
    {
        this.framing = framing;
    }

    /// <summary>设备一侧的编解码工厂，按帧格式选择。</summary>
    public static IFrameCodecFactory For(ModbusFraming framing) => new ModbusDeviceCodecs(framing);

    /// <inheritdoc />
    public IFrameDecoder CreateDecoder()
        => framing == ModbusFraming.Mbap
            ? FrameCodecFactory.Create(ModbusStreamFraming.MbapOptions()).CreateDecoder()
            : new ModbusRtuRequestDecoder();

    /// <inheritdoc />
    public IFrameEncoder CreateEncoder() => PassthroughFrameEncoder.Instance;
}

/// <summary>
/// RTU 请求的分帧器（测试专用）：帧长由请求的功能码与数量决定（第 21.4 节）。CRC 不符或功能码未知时丢弃 1 个字节。
/// </summary>
internal sealed class ModbusRtuRequestDecoder : IFrameDecoder
{
    private const int MaxRequestLength = 256;
    private const int NeedMore = -1;
    private const int Unknown = 0;

    private readonly byte[] window = new byte[MaxRequestLength];

    /// <inheritdoc />
    public bool TryDecode(ref ReadOnlySequence<byte> buffer, out ReadOnlySequence<byte> frame)
    {
        while (buffer.Length >= 2)
        {
            int available = (int)Math.Min(buffer.Length, MaxRequestLength);
            buffer.Slice(0, available).CopyTo(window);
            ReadOnlySpan<byte> head = window.AsSpan(0, available);

            int length = RequestLength(head);
            if (length == NeedMore)
                break;
            if (length == Unknown)
            {
                buffer = buffer.Slice(1);
                continue;
            }

            if (available < length)
                break;

            if (CrcMatches(head.Slice(0, length)))
            {
                frame = buffer.Slice(0, length);
                buffer = buffer.Slice(length);
                return true;
            }

            buffer = buffer.Slice(1);
        }

        frame = default;
        return false;
    }

    private static int RequestLength(ReadOnlySpan<byte> head)
    {
        switch (head[1])
        {
            case ModbusFunctionCodes.ReadCoils:
            case ModbusFunctionCodes.ReadDiscreteInputs:
            case ModbusFunctionCodes.ReadHoldingRegisters:
            case ModbusFunctionCodes.ReadInputRegisters:
            case ModbusFunctionCodes.WriteSingleCoil:
            case ModbusFunctionCodes.WriteSingleRegister:
                return 8;
            case ModbusFunctionCodes.WriteMultipleCoils:
            case ModbusFunctionCodes.WriteMultipleRegisters:
                return head.Length <= 6 ? NeedMore : 9 + head[6];
            case ModbusFunctionCodes.MaskWriteRegister:
                return 10;
            case ModbusFunctionCodes.ReadWriteMultipleRegisters:
                return head.Length <= 10 ? NeedMore : 13 + head[10];
            default:
                return Unknown;
        }
    }

    private static bool CrcMatches(ReadOnlySpan<byte> frame)
    {
        int body = frame.Length - 2;
        ushort received = (ushort)(frame[body] | (frame[body + 1] << 8));
        return ModbusCrc.Compute(frame.Slice(0, body)) == received;
    }
}
