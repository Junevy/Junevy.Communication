using System.Buffers;
using Junevy.Communication.Core.Buffers;
using Junevy.Communication.Core.Results;
using Junevy.Communication.Modbus.Framing;
using Junevy.Communication.Modbus.Protocol;

namespace Junevy.Communication.Modbus;

/// <summary>
/// 帧外壳的选择（Q47）：四种组合（MBAP 或 RTU，字节流或数据报）共用同一套 PDU 编解码，帧格式之间只有外壳不同。
/// </summary>
internal static class ModbusFrames
{
    /// <summary>把 PDU 封装成 MBAP 或 RTU 帧写入 <paramref name="output"/>。MBAP 的事务号由进程级计数器分配。</summary>
    public static void Write(IBufferWriter<byte> output, ModbusFraming framing, byte unitId, ReadOnlySpan<byte> pdu)
    {
        if (framing == ModbusFraming.Mbap)
        {
            MbapHeader.Write(output, MbapHeader.TransactionIds.NextUInt16(), unitId, pdu.Length);
            BigEndianFields.WriteBytes(output, pdu);
        }
        else
        {
            RtuAdu.Write(output, unitId, pdu);
        }
    }

    /// <summary>校验并剥离外壳，返回 PDU。</summary>
    public static CommResult<byte[]> Unwrap(ReadOnlySpan<byte> frame, ModbusFraming framing, byte unitId)
        => framing == ModbusFraming.Mbap ? MbapHeader.Unwrap(frame, unitId) : RtuAdu.Unwrap(frame, unitId);
}
