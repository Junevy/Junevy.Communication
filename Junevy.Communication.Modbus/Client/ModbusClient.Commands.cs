using System.Diagnostics.CodeAnalysis;
using Junevy.Communication.Core.Results;
using Junevy.Communication.Modbus.Protocol;

namespace Junevy.Communication.Modbus;

/// <summary>
/// Modbus 的两个协议专有功能：掩码写寄存器（0x16）与读写多个寄存器（0x17）（P7-D15）。
/// 经套件的协议专有请求入口发送，享有操作锁、重试与错误映射；地址与参数先在这里检查，不合法时不发请求，也不进入操作锁。
/// </summary>
public sealed partial class ModbusClient
{
    private const int MaximumReadWriteReadWords = 125;
    private const int MaximumReadWriteWriteWords = 121;

    /// <summary>
    /// 掩码写保持寄存器（0x16）：结果 = (当前值 AND <paramref name="andMask"/>) OR (<paramref name="orMask"/> AND NOT <paramref name="andMask"/>)，
    /// 由设备原子执行。只发送一个请求；应答必须原样回显地址与两个掩码。
    /// </summary>
    /// <param name="address">保持寄存器地址（<c>HR100</c> 或 <c>40101</c>，不带位号）。</param>
    /// <param name="andMask">与掩码。</param>
    /// <param name="orMask">或掩码。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>写入结果；设备不支持 0x16 时通常为 <c>RemoteError</c>（异常码 01）。</returns>
    public async Task<CommResult> MaskWriteRegisterAsync(string address, ushort andMask, ushort orMask, CancellationToken cancellationToken = default)
    {
        if (!TryParseHoldingRegister(address, out ModbusAddress? target, out CommResult? rejected))
            return rejected;

        byte unit = UnitOf(target);
        ushort register = (ushort)target.Number;
        var expectation = new ModbusExpectation(unit, ModbusFunctionCodes.MaskWriteRegister, 0, Echo(register, andMask, orMask));

        CommResult<bool> result = await SendProtocolRequestAsync(
            "MaskWriteRegisterAsync",
            output =>
            {
                Expect(expectation);
                WriteFrame(output, unit, pdu => ModbusRequestPdu.WriteMaskWriteRegister(pdu, register, andMask, orMask));
            },
            frame => Acknowledge(ParseWriteResponse(frame)),
            cancellationToken).ConfigureAwait(false);

        return result.ToResult();
    }

    /// <summary>
    /// 读写多个保持寄存器（0x17）：设备先写 <paramref name="writeData"/>，再读 <paramref name="readCount"/> 个字。不拆包。
    /// 两个地址必须是不带位号的保持寄存器，并且站号相同。
    /// </summary>
    /// <param name="readAddress">读取的起始地址。</param>
    /// <param name="readCount">读取的字数，1–125。</param>
    /// <param name="writeAddress">写入的起始地址。</param>
    /// <param name="writeData">写入的数据，每个寄存器两字节的大端数据，长度为 2–242 字节。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>读出的字节（<c>2 × readCount</c> 字节，每寄存器大端）。</returns>
    public async Task<CommResult<byte[]>> ReadWriteMultipleRegistersAsync(string readAddress, int readCount, string writeAddress,
                                                                          ReadOnlyMemory<byte> writeData, CancellationToken cancellationToken = default)
    {
        CommResult? rejected = CheckReadWriteMultiple(readAddress, readCount, writeAddress, writeData.Length, out ModbusAddress? read, out ModbusAddress? write);
        if (rejected is not null)
            return rejected.As<byte[]>();

        byte unit = UnitOf(read!);
        ushort readStart = (ushort)read!.Number;
        ushort writeStart = (ushort)write!.Number;
        byte[] data = writeData.ToArray();
        var expectation = new ModbusExpectation(unit, ModbusFunctionCodes.ReadWriteMultipleRegisters, readCount, null);

        return await SendProtocolRequestAsync(
            "ReadWriteMultipleRegistersAsync",
            output =>
            {
                Expect(expectation);
                WriteFrame(output, unit, pdu => ModbusRequestPdu.WriteReadWriteMultipleRegisters(pdu, readStart, (ushort)readCount, writeStart, data));
            },
            frame => ParseReadWordsResponse(readCount, frame),
            cancellationToken).ConfigureAwait(false);
    }

    // 掩码写的地址：必须能解析，并且是不带位号的保持寄存器（IR、C、DI 与带位号的地址都返回 InvalidRequest）。
    private bool TryParseHoldingRegister(string address, [NotNullWhen(true)] out ModbusAddress? target, [NotNullWhen(false)] out CommResult? rejected)
    {
        if (!TryParseAddress(address ?? string.Empty, out target, out string? error))
        {
            rejected = CommResult.Fail(error ?? "The address is not valid.", CommErrorKind.InvalidRequest);
            return false;
        }

        if (!IsPlainHoldingRegister(target))
        {
            rejected = CommResult.Fail($"The mask write needs a holding register without a bit index; '{address}' is not one.", CommErrorKind.InvalidRequest);
            return false;
        }

        rejected = null;
        return true;
    }

    // 读写多个：两个地址都是不带位号的保持寄存器，站号相同，字数与数据长度在范围内，两段的结束地址不超过 65535。不拆包。
    private CommResult? CheckReadWriteMultiple(string readAddress, int readCount, string writeAddress, int writeLength,
                                               out ModbusAddress? read, out ModbusAddress? write)
    {
        read = null;
        write = null;
        if (!TryParseAddress(readAddress ?? string.Empty, out read, out string? readError))
            return CommResult.Fail(readError ?? "The read address is not valid.", CommErrorKind.InvalidRequest);
        if (!TryParseAddress(writeAddress ?? string.Empty, out write, out string? writeError))
            return CommResult.Fail(writeError ?? "The write address is not valid.", CommErrorKind.InvalidRequest);

        if (!IsPlainHoldingRegister(read) || !IsPlainHoldingRegister(write))
            return CommResult.Fail("Both addresses must be holding registers without a bit index.", CommErrorKind.InvalidRequest);
        if (UnitOf(read) != UnitOf(write))
            return CommResult.Fail("The read and write addresses must name the same station.", CommErrorKind.InvalidRequest);
        if (readCount < 1 || readCount > MaximumReadWriteReadWords)
            return CommResult.Fail($"The read count must be between 1 and {MaximumReadWriteReadWords} registers.", CommErrorKind.InvalidRequest);

        int writeWords = writeLength / BytesPerWord;
        if (writeLength < BytesPerWord || writeLength % BytesPerWord != 0 || writeWords > MaximumReadWriteWriteWords)
        {
            return CommResult.Fail($"The write data must be 1 to {MaximumReadWriteWriteWords} whole registers (2 to {MaximumReadWriteWriteWords * BytesPerWord} bytes).",
                                   CommErrorKind.InvalidRequest);
        }

        if (read.Number + readCount - 1 > MaximumAddress || write.Number + writeWords - 1 > MaximumAddress)
            return CommResult.Fail($"A register range ends beyond address {MaximumAddress}.", CommErrorKind.InvalidRequest);

        return null;
    }

    private static bool IsPlainHoldingRegister(ModbusAddress address)
        => address.Device == ModbusDevice.HoldingRegister && address.BitIndex is null;

    private static CommResult<bool> Acknowledge(CommResult result)
        => result.IsSuccess ? CommResult<bool>.Success(true) : result.As<bool>();
}
