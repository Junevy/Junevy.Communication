using System.Buffers;
using System.Diagnostics.CodeAnalysis;
using Junevy.Communication.Core.Buffers;
using Junevy.Communication.Core.Results;
using Junevy.Communication.Modbus.Protocol;
using Junevy.Communication.Plc;

namespace Junevy.Communication.Modbus;

/// <summary>
/// <see cref="ModbusClient"/> 的协议钩子（P7-D4 至 D6、D10、D15）：地址、单包上限、功能码映射、只读与站号的检查，
/// 以及每个请求的编码与应答的解码。字节流与数据报的外壳由 <see cref="ModbusFrames"/> 处理。
/// Plc 的钩子是 protected internal，本程序集不是它的友元，因此这里用 protected override（与 MELSEC 一致）。
/// </summary>
public sealed partial class ModbusClient
{
    private const int MaximumAddress = 65535;
    private const int BitsPerRegister = 16;
    private const int MaximumReadWords = 125;
    private const int MaximumWriteWords = 123;
    private const int MaximumReadBits = 2000;
    private const int MaximumWriteBits = 1968;
    private const int BytesPerWord = 2;

    /// <inheritdoc />
    protected override bool TryParseAddress(string address, [NotNullWhen(true)] out ModbusAddress? parsed, [NotNullWhen(false)] out string? error)
    {
        if (!ModbusAddressParser.TryParse(address, out parsed, out error))
            return false;

        // 站号的范围由帧格式决定（P7-D4）：RTU 为 1–247（0 是广播，不做；248–255 是保留值）。
        if (framing == ModbusFraming.Rtu && parsed.UnitId is { } unit && !RtuAdu.IsValidUnitId(unit))
        {
            parsed = null;
            error = $"The RTU unit ID in address '{address}' must be between {RtuAdu.MinimumUnitId} and {RtuAdu.MaximumUnitId}.";
            return false;
        }

        return true;
    }

    /// <inheritdoc />
    protected override int GetMaxReadPoints(ModbusAddress address, PlcAccess access) => access switch
    {
        PlcAccess.Bits => MaximumReadBits,
        PlcAccess.Bytes => MaximumReadWords * BytesPerWord,
        _ => MaximumReadWords,
    };

    /// <inheritdoc />
    protected override int GetMaxWritePoints(ModbusAddress address, PlcAccess access) => access switch
    {
        // 寄存器中的位用掩码写（0x16）：每个请求只改一位（P7-D6）。
        PlcAccess.Bits when address.Device == ModbusDevice.HoldingRegister => 1,
        PlcAccess.Bits => MaximumWriteBits,
        PlcAccess.Bytes => MaximumWriteWords * BytesPerWord,
        _ => MaximumWriteWords,
    };

    /// <inheritdoc />
    protected override CommResult ValidateRange(ModbusAddress address, PlcAccess access, int count)
    {
        ModbusDevice device = address.Device;
        if (device.Unit == PlcAddressUnit.Bit && access != PlcAccess.Bits)
        {
            return CommResult.Fail($"Coils and discrete inputs can only be accessed by bit; use ReadBitsAsync or WriteBitsAsync for '{address.Text}'.",
                                   CommErrorKind.InvalidRequest);
        }

        long span = access switch
        {
            // 寄存器中的位：按位置计算覆盖的字数。
            PlcAccess.Bits when device.Unit != PlcAddressUnit.Bit => (((address.BitIndex ?? 0) + count) + BitsPerRegister - 1) / BitsPerRegister,
            PlcAccess.Bytes => (count + BytesPerWord - 1) / BytesPerWord,
            _ => count,
        };

        long last = address.Number + span - 1;
        if (last > MaximumAddress)
        {
            return CommResult.Fail($"The address '{address.Text}' with {count} point(s) ends beyond address {MaximumAddress}.",
                                   CommErrorKind.InvalidRequest);
        }

        return CommResult.Success();
    }

    /// <inheritdoc />
    protected override bool IsReadOnly(ModbusAddress address) => address.Device.IsReadOnly;

    /// <inheritdoc />
    protected override bool CanWriteBitsNatively(ModbusAddress address)
        => address.Unit == PlcAddressUnit.Bit || (address.Device == ModbusDevice.HoldingRegister && settings.UseMaskWriteForBits);

    /// <inheritdoc />
    protected override void BuildReadWordsRequest(ModbusAddress address, int offset, int count, IBufferWriter<byte> output)
    {
        byte unit = UnitOf(address);
        byte function = address.Device.ReadFunctionCode;
        ushort start = StartOf(address, offset);
        Expect(new ModbusExpectation(unit, function, count, null));
        WriteFrame(output, unit, pdu => ModbusRequestPdu.WriteReadRequest(pdu, function, start, (ushort)count));
    }

    /// <inheritdoc />
    protected override CommResult<byte[]> ParseReadWordsResponse(int count, ReadOnlySpan<byte> frame)
    {
        ModbusExpectation expectation = TakeExpectation();
        CommResult<byte[]> pdu = ModbusFrames.Unwrap(frame, framing, expectation.UnitId);
        return pdu.IsSuccess ? ModbusResponsePdu.DecodeWords(pdu.Data, expectation) : pdu;
    }

    /// <inheritdoc />
    protected override void BuildWriteWordsRequest(ModbusAddress address, int offset, ReadOnlySpan<byte> words, IBufferWriter<byte> output)
    {
        byte unit = UnitOf(address);
        ushort start = StartOf(address, offset);
        int registers = words.Length / BytesPerWord;

        // 单功能码规则（P7-D5）：一个点的包用 06，否则用 10；规则按包判断。
        if (registers == 1 && settings.UseSingleWriteFunctions)
        {
            ushort value = (ushort)((words[0] << 8) | words[1]);
            Expect(new ModbusExpectation(unit, ModbusFunctionCodes.WriteSingleRegister, 0, Echo(start, value)));
            WriteFrame(output, unit, pdu => ModbusRequestPdu.WriteWriteSingleRegister(pdu, start, value));
            return;
        }

        byte[] data = words.ToArray();
        Expect(new ModbusExpectation(unit, ModbusFunctionCodes.WriteMultipleRegisters, 0, Echo(start, (ushort)registers)));
        WriteFrame(output, unit, pdu => ModbusRequestPdu.WriteWriteMultipleRegisters(pdu, start, data));
    }

    /// <inheritdoc />
    protected override void BuildReadBitsRequest(ModbusAddress address, int offset, int count, IBufferWriter<byte> output)
    {
        byte unit = UnitOf(address);
        byte function = address.Device.ReadFunctionCode;
        ushort start = StartOf(address, offset);
        Expect(new ModbusExpectation(unit, function, count, null));
        WriteFrame(output, unit, pdu => ModbusRequestPdu.WriteReadRequest(pdu, function, start, (ushort)count));
    }

    /// <inheritdoc />
    protected override CommResult<bool[]> ParseReadBitsResponse(int count, ReadOnlySpan<byte> frame)
    {
        ModbusExpectation expectation = TakeExpectation();
        CommResult<byte[]> pdu = ModbusFrames.Unwrap(frame, framing, expectation.UnitId);
        return pdu.IsSuccess ? ModbusResponsePdu.DecodeBits(pdu.Data, expectation) : pdu.As<bool[]>();
    }

    /// <inheritdoc />
    protected override void BuildWriteBitsRequest(ModbusAddress address, int offset, ReadOnlySpan<bool> values, IBufferWriter<byte> output)
    {
        byte unit = UnitOf(address);
        if (address.Device.Unit == PlcAddressUnit.Bit)
        {
            // 线圈：一个点用 05（FF 00 或 00 00），否则用 0F。
            ushort start = StartOf(address, offset);
            bool[] data = values.ToArray();
            if (data.Length == 1 && settings.UseSingleWriteFunctions)
            {
                bool value = data[0];
                ushort raw = value ? (ushort)0xFF00 : (ushort)0;
                Expect(new ModbusExpectation(unit, ModbusFunctionCodes.WriteSingleCoil, 0, Echo(start, raw)));
                WriteFrame(output, unit, pdu => ModbusRequestPdu.WriteWriteSingleCoil(pdu, start, value));
                return;
            }

            Expect(new ModbusExpectation(unit, ModbusFunctionCodes.WriteMultipleCoils, 0, Echo(start, (ushort)data.Length)));
            WriteFrame(output, unit, pdu => ModbusRequestPdu.WriteWriteMultipleCoils(pdu, start, data));
            return;
        }

        // 寄存器中的位：掩码写（0x16）。绝对位置 = 编号 × 16 + 位号 + 偏移（Plc 套件的约定）。
        long absolute = (long)address.Number * BitsPerRegister + (address.BitIndex ?? 0) + offset;
        ushort register = (ushort)(absolute / BitsPerRegister);
        ushort mask = (ushort)(1 << (int)(absolute % BitsPerRegister));
        ushort andMask = (ushort)~mask;
        ushort orMask = values[0] ? mask : (ushort)0;
        Expect(new ModbusExpectation(unit, ModbusFunctionCodes.MaskWriteRegister, 0, Echo(register, andMask, orMask)));
        WriteFrame(output, unit, pdu => ModbusRequestPdu.WriteMaskWriteRegister(pdu, register, andMask, orMask));
    }

    /// <inheritdoc />
    protected override CommResult ParseWriteResponse(ReadOnlySpan<byte> frame)
    {
        ModbusExpectation expectation = TakeExpectation();
        CommResult<byte[]> pdu = ModbusFrames.Unwrap(frame, framing, expectation.UnitId);
        return pdu.IsSuccess ? ModbusResponsePdu.DecodeEcho(pdu.Data, expectation) : pdu.ToResult();
    }

    // 请求的站号：地址的站号前缀优先，否则用选项中的默认站号。
    private byte UnitOf(ModbusAddress address) => address.UnitId ?? settings.UnitId;

    private static ushort StartOf(ModbusAddress address, int offset) => (ushort)(address.Number + offset);

    // 先把 PDU 写入临时缓冲，再由帧外壳封装到 output（MBAP 头与 RTU 的 CRC 都依赖 PDU 的长度与内容）。
    private void WriteFrame(IBufferWriter<byte> output, byte unitId, Action<IBufferWriter<byte>> writePdu)
    {
        using var pdu = new ByteBufferWriter();
        writePdu(pdu);
        ModbusFrames.Write(output, framing, unitId, pdu.WrittenSpan);
    }

    // 写入应答的回显：16 位大端的字段依次拼接。
    private static byte[] Echo(params ushort[] fields)
    {
        var bytes = new byte[fields.Length * BytesPerWord];
        for (int i = 0; i < fields.Length; i++)
        {
            bytes[i * BytesPerWord] = (byte)(fields[i] >> 8);
            bytes[i * BytesPerWord + 1] = (byte)fields[i];
        }

        return bytes;
    }
}
