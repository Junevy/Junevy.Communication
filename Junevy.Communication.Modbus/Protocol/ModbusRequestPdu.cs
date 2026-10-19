using System.Buffers;
using Junevy.Communication.Core.Buffers;

namespace Junevy.Communication.Modbus.Protocol;

/// <summary>
/// PDU 请求的编码（第 21.1 节）：只写功能码与数据，不含站号与帧封装。字节按规范为大端；线圈值为 <c>FF 00</c> / <c>00 00</c>。
/// </summary>
internal static class ModbusRequestPdu
{
    private const ushort CoilOn = 0xFF00;
    private const ushort CoilOff = 0x0000;

    /// <summary>读取请求（01、02、03、04）：起始地址与点数或字数。</summary>
    public static void WriteReadRequest(IBufferWriter<byte> output, byte functionCode, ushort address, ushort quantity)
    {
        BigEndianFields.WriteUInt8(output, functionCode);
        BigEndianFields.WriteUInt16(output, address);
        BigEndianFields.WriteUInt16(output, quantity);
    }

    /// <summary>写单个线圈（05）。</summary>
    public static void WriteWriteSingleCoil(IBufferWriter<byte> output, ushort address, bool value)
    {
        BigEndianFields.WriteUInt8(output, ModbusFunctionCodes.WriteSingleCoil);
        BigEndianFields.WriteUInt16(output, address);
        BigEndianFields.WriteUInt16(output, value ? CoilOn : CoilOff);
    }

    /// <summary>写单个寄存器（06）。</summary>
    public static void WriteWriteSingleRegister(IBufferWriter<byte> output, ushort address, ushort value)
    {
        BigEndianFields.WriteUInt8(output, ModbusFunctionCodes.WriteSingleRegister);
        BigEndianFields.WriteUInt16(output, address);
        BigEndianFields.WriteUInt16(output, value);
    }

    /// <summary>写多个线圈（0F）：点数与打包的字节，第一个点在最低位。</summary>
    public static void WriteWriteMultipleCoils(IBufferWriter<byte> output, ushort address, ReadOnlySpan<bool> values)
    {
        var packed = new byte[ModbusBits.ByteCount(values.Length)];
        ModbusBits.Pack(values, packed);

        BigEndianFields.WriteUInt8(output, ModbusFunctionCodes.WriteMultipleCoils);
        BigEndianFields.WriteUInt16(output, address);
        BigEndianFields.WriteUInt16(output, (ushort)values.Length);
        BigEndianFields.WriteUInt8(output, (byte)packed.Length);
        BigEndianFields.WriteBytes(output, packed);
    }

    /// <summary>写多个寄存器（10）：<paramref name="words"/> 是每寄存器两字节的大端数据，长度为偶数。</summary>
    public static void WriteWriteMultipleRegisters(IBufferWriter<byte> output, ushort address, ReadOnlySpan<byte> words)
    {
        BigEndianFields.WriteUInt8(output, ModbusFunctionCodes.WriteMultipleRegisters);
        BigEndianFields.WriteUInt16(output, address);
        BigEndianFields.WriteUInt16(output, (ushort)(words.Length / 2));
        BigEndianFields.WriteUInt8(output, (byte)words.Length);
        BigEndianFields.WriteBytes(output, words);
    }

    /// <summary>掩码写寄存器（16）：结果 = (当前值 AND andMask) OR (orMask AND NOT andMask)。</summary>
    public static void WriteMaskWriteRegister(IBufferWriter<byte> output, ushort address, ushort andMask, ushort orMask)
    {
        BigEndianFields.WriteUInt8(output, ModbusFunctionCodes.MaskWriteRegister);
        BigEndianFields.WriteUInt16(output, address);
        BigEndianFields.WriteUInt16(output, andMask);
        BigEndianFields.WriteUInt16(output, orMask);
    }

    /// <summary>读写多个寄存器（17）：设备先写后读；<paramref name="writeWords"/> 是每寄存器两字节的大端数据。</summary>
    public static void WriteReadWriteMultipleRegisters(IBufferWriter<byte> output, ushort readAddress, ushort readQuantity, ushort writeAddress,
                                                       ReadOnlySpan<byte> writeWords)
    {
        BigEndianFields.WriteUInt8(output, ModbusFunctionCodes.ReadWriteMultipleRegisters);
        BigEndianFields.WriteUInt16(output, readAddress);
        BigEndianFields.WriteUInt16(output, readQuantity);
        BigEndianFields.WriteUInt16(output, writeAddress);
        BigEndianFields.WriteUInt16(output, (ushort)(writeWords.Length / 2));
        BigEndianFields.WriteUInt8(output, (byte)writeWords.Length);
        BigEndianFields.WriteBytes(output, writeWords);
    }
}
