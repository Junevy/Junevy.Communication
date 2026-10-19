using System.Buffers;
using Junevy.Communication.Channels;
using Junevy.Communication.Modbus.Protocol;

namespace Junevy.Communication.Modbus.Framing;

/// <summary>
/// RTU 应答的分帧器（P7-D8）：帧长由应答的功能码与字节数确定（第 21.4 节），收全后以 CRC 裁决。
/// 功能码未知、字节数超过规范上限或 CRC 不符时，丢弃 1 个字节并重新同步，不抛异常（线路噪声不会让通道断开或丢掉缓冲）。
/// </summary>
internal sealed class ModbusRtuFrameDecoder : IFrameDecoder
{
    private const int MaxFrameLength = 256;
    private const int ByteCountIndex = 2;

    // 读应答的字节数上限：125 个寄存器或 2000 个点都是 250 字节（规范第 6.1–6.4 节），更大的只可能是噪声。
    private const int MaxReadByteCount = 250;
    private const int ReadOverhead = 3 + 2;   // 站号、功能码、字节数，加 CRC
    private const int ExceptionLength = 5;    // 站号、功能码、异常码，加 CRC
    private const int WriteEchoLength = 8;    // 05、06、0F、10
    private const int MaskWriteLength = 10;   // 16
    private const int CrcLength = 2;
    private const int Unknown = 0;
    private const int NeedMore = -1;

    // 窗口的复制缓冲：一个候选帧最多占用 MaxFrameLength 字节。
    private readonly byte[] window = new byte[MaxFrameLength];

    /// <inheritdoc />
    public bool TryDecode(ref ReadOnlySequence<byte> buffer, out ReadOnlySequence<byte> frame)
    {
        while (buffer.Length >= 2)
        {
            int available = (int)Math.Min(buffer.Length, MaxFrameLength);
            buffer.Slice(0, available).CopyTo(window);
            ReadOnlySpan<byte> head = window.AsSpan(0, available);

            int length = FrameLength(head);
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

    // 由应答的功能码确定帧长。返回 NeedMore 表示还要等字节数字段；返回 Unknown 表示功能码不认识或字节数超过上限。
    private static int FrameLength(ReadOnlySpan<byte> head)
    {
        byte function = head[1];
        if ((function & ModbusFunctionCodes.ExceptionFlag) != 0)
            return IsKnownFunction((byte)(function & ~ModbusFunctionCodes.ExceptionFlag)) ? ExceptionLength : Unknown;

        switch (function)
        {
            case ModbusFunctionCodes.ReadCoils:
            case ModbusFunctionCodes.ReadDiscreteInputs:
            case ModbusFunctionCodes.ReadHoldingRegisters:
            case ModbusFunctionCodes.ReadInputRegisters:
            case ModbusFunctionCodes.ReadWriteMultipleRegisters:
                if (head.Length <= ByteCountIndex)
                    return NeedMore;
                return head[ByteCountIndex] > MaxReadByteCount ? Unknown : ReadOverhead + head[ByteCountIndex];
            case ModbusFunctionCodes.WriteSingleCoil:
            case ModbusFunctionCodes.WriteSingleRegister:
            case ModbusFunctionCodes.WriteMultipleCoils:
            case ModbusFunctionCodes.WriteMultipleRegisters:
                return WriteEchoLength;
            case ModbusFunctionCodes.MaskWriteRegister:
                return MaskWriteLength;
            default:
                return Unknown;
        }
    }

    private static bool IsKnownFunction(byte function) => function is ModbusFunctionCodes.ReadCoils
        or ModbusFunctionCodes.ReadDiscreteInputs
        or ModbusFunctionCodes.ReadHoldingRegisters
        or ModbusFunctionCodes.ReadInputRegisters
        or ModbusFunctionCodes.WriteSingleCoil
        or ModbusFunctionCodes.WriteSingleRegister
        or ModbusFunctionCodes.WriteMultipleCoils
        or ModbusFunctionCodes.WriteMultipleRegisters
        or ModbusFunctionCodes.MaskWriteRegister
        or ModbusFunctionCodes.ReadWriteMultipleRegisters;

    // 帧尾两字节是 CRC（低字节在前），与前面的字节计算出的 CRC 相等即为完整的帧。
    private static bool CrcMatches(ReadOnlySpan<byte> frame)
    {
        int body = frame.Length - CrcLength;
        ushort received = (ushort)(frame[body] | (frame[body + 1] << 8));
        return ModbusCrc.Compute(frame.Slice(0, body)) == received;
    }
}
