using System.Buffers;
using Junevy.Communication.Channels;
using Junevy.Communication.Core.Buffers;
using Junevy.Communication.Core.Results;
using Junevy.Communication.Modbus.Protocol;

namespace Junevy.Communication.Modbus;

/// <summary>
/// Modbus 的心跳探测（P7-D12）：向默认站号读一个保持寄存器（HR0，1 个字）。收到格式正确的应答（站号与功能码相符，正常或异常应答都算）即为健康。
/// Modbus 没有通用的回环命令（诊断类功能码只用于串行链路，很多 TCP 设备不支持），因此用读取代替。
/// </summary>
internal sealed class ModbusReadProbe : IHealthProbe
{
    private const ushort ProbeAddress = 0;
    private const ushort ProbeQuantity = 1;

    private readonly IByteChannel channel;
    private readonly ModbusFraming framing;
    private readonly byte unitId;

    /// <summary>创建探测。</summary>
    /// <param name="channel">底层字节通道。</param>
    /// <param name="framing">帧格式（已按介质解析）。</param>
    /// <param name="unitId">默认站号。</param>
    public ModbusReadProbe(IByteChannel channel, ModbusFraming framing, byte unitId)
    {
        this.channel = channel;
        this.framing = framing;
        this.unitId = unitId;
    }

    /// <inheritdoc />
    public async Task<CommResult> ProbeAsync(CancellationToken cancellationToken)
    {
        CommResult<byte[]> response = await channel.RequestAsync(BuildRequest(), null, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccess)
            return response.ToResult();

        CommResult<byte[]> pdu = Unwrap(response.Data);
        if (!pdu.IsSuccess)
            return pdu.ToResult();

        var expectation = new ModbusExpectation(unitId, ModbusFunctionCodes.ReadHoldingRegisters, ProbeQuantity, null);
        CommResult<byte[]> decoded = ModbusResponsePdu.DecodeWords(pdu.Data, expectation);

        // 异常应答也说明链路与协议栈正常（P4-D15 的原则）；格式或站号不符才是不健康。
        return decoded.IsSuccess || decoded.ErrorKind == CommErrorKind.RemoteError ? CommResult.Success() : decoded.ToResult();
    }

    private byte[] BuildRequest()
    {
        using var pdu = new ByteBufferWriter();
        ModbusRequestPdu.WriteReadRequest(pdu, ModbusFunctionCodes.ReadHoldingRegisters, ProbeAddress, ProbeQuantity);

        using var frame = new ByteBufferWriter();
        ModbusFrames.Write(frame, framing, unitId, pdu.WrittenSpan);

        return frame.ToArray();
    }

    private CommResult<byte[]> Unwrap(byte[] frame) => ModbusFrames.Unwrap(frame, framing, unitId);
}
