using Junevy.Communication.DebugAssistant.Simulators.Memory;

namespace Junevy.Communication.DebugAssistant.Simulators.Modbus;

/// <summary>一个 PDU 的处理结果：应答 PDU（异常为两字节）与请求日志的摘要。</summary>
internal sealed record ModbusPduResult(byte[] Pdu, string Summary);

/// <summary>
/// Modbus 的 PDU 处理（计划第 8.1 节）：01–06、0F、10、16、17。数据区为 C（线圈）、DI（离散输入）、HR（保持寄存器）、IR（输入寄存器），
/// 按协议地址（零基）访问。异常：01 功能码不支持，02 地址越界，03 数量或数据不合法。
/// </summary>
internal sealed class ModbusPduProcessor(BitArea coils, BitArea inputs, WordArea holding, WordArea registers)
{
    private const byte IllegalFunction = 0x01;
    private const byte IllegalAddress = 0x02;
    private const byte IllegalValue = 0x03;
    private const int MaxReadBits = 2000;
    private const int MaxWriteBits = 1968;
    private const int MaxReadWords = 125;
    private const int MaxWriteWords = 123;
    private const int MaxReadWriteWords = 121;

    public ModbusPduResult Process(ReadOnlySpan<byte> pdu)
    {
        byte function = pdu[0];
        try
        {
            return function switch
            {
                0x01 or 0x02 => ReadBits(function, pdu),
                0x03 or 0x04 => ReadWords(function, pdu),
                0x05 => WriteCoil(pdu),
                0x06 => WriteRegister(pdu),
                0x0F => WriteCoils(pdu),
                0x10 => WriteRegisters(pdu),
                0x16 => MaskWrite(pdu),
                0x17 => ReadWriteRegisters(pdu),
                _ => throw new ModbusException(IllegalFunction),
            };
        }
        catch (ModbusException ex)
        {
            return new ModbusPduResult([(byte)(function | 0x80), ex.ExceptionCode], $"{function:X2} {FunctionName(function)}：异常 {ex.ExceptionCode:X2}");
        }
    }

    public static string FunctionName(byte function) => function switch
    {
        0x01 => "读线圈",
        0x02 => "读离散输入",
        0x03 => "读保持寄存器",
        0x04 => "读输入寄存器",
        0x05 => "写单个线圈",
        0x06 => "写单个寄存器",
        0x0F => "写多个线圈",
        0x10 => "写多个寄存器",
        0x16 => "掩码写寄存器",
        0x17 => "读写多个寄存器",
        _ => "未知功能码",
    };

    private ModbusPduResult ReadBits(byte function, ReadOnlySpan<byte> pdu)
    {
        RequireLength(pdu, 5);
        int start = Word(pdu, 1);
        int count = Word(pdu, 3);
        RequireQuantity(count, MaxReadBits);
        BitArea area = function == 0x01 ? coils : inputs;
        CheckAddress(area.Length, start, count);

        byte[] packed = PackBits(area.Read(start, count));
        return new ModbusPduResult([function, (byte)packed.Length, .. packed], Describe(function, area.Name, start, count));
    }

    private ModbusPduResult ReadWords(byte function, ReadOnlySpan<byte> pdu)
    {
        RequireLength(pdu, 5);
        int start = Word(pdu, 1);
        int count = Word(pdu, 3);
        RequireQuantity(count, MaxReadWords);
        WordArea area = function == 0x03 ? holding : registers;
        CheckAddress(area.Length, start, count);

        ushort[] values = area.Read(start, count);
        byte[] data = new byte[values.Length * 2];
        for (int index = 0; index < values.Length; index++)
        {
            data[index * 2] = (byte)(values[index] >> 8);
            data[(index * 2) + 1] = (byte)values[index];
        }

        return new ModbusPduResult([function, (byte)data.Length, .. data], Describe(function, area.Name, start, count));
    }

    private ModbusPduResult WriteCoil(ReadOnlySpan<byte> pdu)
    {
        RequireLength(pdu, 5);
        int address = Word(pdu, 1);
        int value = Word(pdu, 3);
        if (value is not (0xFF00 or 0x0000))
        {
            throw new ModbusException(IllegalValue);
        }

        CheckAddress(coils.Length, address, 1);
        coils.Write(address, [value == 0xFF00]);
        return new ModbusPduResult(pdu.ToArray(), Describe(0x05, coils.Name, address, 1));
    }

    private ModbusPduResult WriteRegister(ReadOnlySpan<byte> pdu)
    {
        RequireLength(pdu, 5);
        int address = Word(pdu, 1);
        CheckAddress(holding.Length, address, 1);
        holding.Write(address, [(ushort)Word(pdu, 3)]);
        return new ModbusPduResult(pdu.ToArray(), Describe(0x06, holding.Name, address, 1));
    }

    private ModbusPduResult WriteCoils(ReadOnlySpan<byte> pdu)
    {
        RequireLength(pdu, 6);
        int start = Word(pdu, 1);
        int count = Word(pdu, 3);
        RequireQuantity(count, MaxWriteBits);
        int byteCount = (count + 7) / 8;
        if (pdu[5] != byteCount)
        {
            throw new ModbusException(IllegalValue);
        }

        RequireLength(pdu, 6 + byteCount);
        CheckAddress(coils.Length, start, count);
        bool[] bits = new bool[count];
        for (int index = 0; index < count; index++)
        {
            bits[index] = (pdu[6 + (index / 8)] & (1 << (index % 8))) != 0;
        }

        coils.Write(start, bits);
        return new ModbusPduResult(pdu[..5].ToArray(), Describe(0x0F, coils.Name, start, count));
    }

    private ModbusPduResult WriteRegisters(ReadOnlySpan<byte> pdu)
    {
        RequireLength(pdu, 6);
        int start = Word(pdu, 1);
        int count = Word(pdu, 3);
        RequireQuantity(count, MaxWriteWords);
        if (pdu[5] != count * 2)
        {
            throw new ModbusException(IllegalValue);
        }

        RequireLength(pdu, 6 + (count * 2));
        CheckAddress(holding.Length, start, count);
        holding.Write(start, ReadWords(pdu.Slice(6, count * 2)));
        return new ModbusPduResult(pdu[..5].ToArray(), Describe(0x10, holding.Name, start, count));
    }

    private ModbusPduResult MaskWrite(ReadOnlySpan<byte> pdu)
    {
        RequireLength(pdu, 7);
        int address = Word(pdu, 1);
        ushort andMask = (ushort)Word(pdu, 3);
        ushort orMask = (ushort)Word(pdu, 5);
        CheckAddress(holding.Length, address, 1);

        ushort current = holding.Read(address, 1)[0];
        holding.Write(address, [(ushort)((current & andMask) | (orMask & ~andMask))]);
        return new ModbusPduResult(pdu.ToArray(), Describe(0x16, holding.Name, address, 1));
    }

    private ModbusPduResult ReadWriteRegisters(ReadOnlySpan<byte> pdu)
    {
        RequireLength(pdu, 10);
        int readStart = Word(pdu, 1);
        int readCount = Word(pdu, 3);
        int writeStart = Word(pdu, 5);
        int writeCount = Word(pdu, 7);
        RequireQuantity(readCount, MaxReadWords);
        RequireQuantity(writeCount, MaxReadWriteWords);
        if (pdu[9] != writeCount * 2)
        {
            throw new ModbusException(IllegalValue);
        }

        RequireLength(pdu, 10 + (writeCount * 2));
        CheckAddress(holding.Length, readStart, readCount);
        CheckAddress(holding.Length, writeStart, writeCount);

        // 协议规定先写后读。
        holding.Write(writeStart, ReadWords(pdu.Slice(10, writeCount * 2)));
        ushort[] values = holding.Read(readStart, readCount);
        byte[] data = new byte[values.Length * 2];
        for (int index = 0; index < values.Length; index++)
        {
            data[index * 2] = (byte)(values[index] >> 8);
            data[(index * 2) + 1] = (byte)values[index];
        }

        return new ModbusPduResult([0x17, (byte)data.Length, .. data], $"{Describe(0x17, holding.Name, readStart, readCount)}；写 {holding.Name}{writeStart} 数量 {writeCount}");
    }

    private static string Describe(byte function, string area, int start, int count) =>
        $"{function:X2} {FunctionName(function)} {area}{start} 数量 {count}";

    private static int Word(ReadOnlySpan<byte> pdu, int offset) => (pdu[offset] << 8) | pdu[offset + 1];

    private static void RequireLength(ReadOnlySpan<byte> pdu, int length)
    {
        if (pdu.Length < length)
        {
            throw new ModbusException(IllegalValue);
        }
    }

    private static void RequireQuantity(int count, int max)
    {
        if (count is < 1 || count > max)
        {
            throw new ModbusException(IllegalValue);
        }
    }

    private static void CheckAddress(int length, int start, int count)
    {
        if (start + count > length)
        {
            throw new ModbusException(IllegalAddress);
        }
    }

    private static byte[] PackBits(bool[] bits)
    {
        byte[] packed = new byte[(bits.Length + 7) / 8];
        for (int index = 0; index < bits.Length; index++)
        {
            if (bits[index])
            {
                packed[index / 8] |= (byte)(1 << (index % 8));
            }
        }

        return packed;
    }

    private static ushort[] ReadWords(ReadOnlySpan<byte> data)
    {
        ushort[] words = new ushort[data.Length / 2];
        for (int index = 0; index < words.Length; index++)
        {
            words[index] = (ushort)((data[index * 2] << 8) | data[(index * 2) + 1]);
        }

        return words;
    }
}

/// <summary>协议异常：携带异常码，由 <see cref="ModbusPduProcessor"/> 转为异常应答。</summary>
internal sealed class ModbusException(byte exceptionCode) : Exception
{
    public byte ExceptionCode { get; } = exceptionCode;
}
