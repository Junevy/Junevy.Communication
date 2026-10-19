using Junevy.Communication.Modbus.Protocol;
using Junevy.Communication.Testing;

namespace Junevy.Communication.Modbus.Tests;

/// <summary>模拟从站的写入与读写多个的处理（与 <c>ModbusSlave.cs</c> 的读取与分派同一个类）。</summary>
internal sealed partial class ModbusSlave
{
    private byte[] WriteSingleCoil(byte[] pdu)
    {
        int address = Be16(pdu, 1);
        CheckRange(address, 1);
        memory.SetBits("C", address, Be16(pdu, 3) == 0xFF00);
        return pdu.Take(5).ToArray();
    }

    private byte[] WriteSingleRegister(byte[] pdu)
    {
        int address = Be16(pdu, 1);
        CheckRange(address, 1);
        memory.SetWords("HR", address, (ushort)Be16(pdu, 3));
        return pdu.Take(5).ToArray();
    }

    private byte[] WriteMultipleCoils(byte[] pdu)
    {
        int address = Be16(pdu, 1);
        int count = Be16(pdu, 3);
        CheckRange(address, count);
        var values = new bool[count];
        for (int i = 0; i < count; i++)
            values[i] = ((pdu[6 + i / 8] >> (i % 8)) & 1) != 0;

        memory.SetBits("C", address, values);
        return pdu.Take(5).ToArray();
    }

    private byte[] WriteMultipleRegisters(byte[] pdu)
    {
        int address = Be16(pdu, 1);
        int count = Be16(pdu, 3);
        CheckRange(address, count);
        var words = new ushort[count];
        for (int i = 0; i < count; i++)
            words[i] = (ushort)Be16(pdu, 6 + i * 2);

        memory.SetWords("HR", address, words);
        return pdu.Take(5).ToArray();
    }

    private byte[] MaskWrite(byte[] pdu)
    {
        int address = Be16(pdu, 1);
        CheckRange(address, 1);
        ushort andMask = (ushort)Be16(pdu, 3);
        ushort orMask = (ushort)Be16(pdu, 5);
        ushort current = memory.GetWords("HR", address, 1)[0];
        memory.SetWords("HR", address, (ushort)((current & andMask) | (orMask & ~andMask)));
        return pdu.Take(7).ToArray();
    }

    private byte[] ReadWriteMultiple(byte[] pdu)
    {
        int readAddress = Be16(pdu, 1);
        int readCount = Be16(pdu, 3);
        int writeAddress = Be16(pdu, 5);
        int writeCount = Be16(pdu, 7);
        CheckRange(writeAddress, writeCount);
        CheckRange(readAddress, readCount);

        var words = new ushort[writeCount];
        for (int i = 0; i < writeCount; i++)
            words[i] = (ushort)Be16(pdu, 10 + i * 2);

        memory.SetWords("HR", writeAddress, words);
        ushort[] read = memory.GetWords("HR", readAddress, readCount);
        return Concat(new[] { pdu[0], (byte)(readCount * 2) }, Words(read));
    }
}
