using Junevy.Communication.Modbus.Core.Models;

namespace Junevy.Communication.Modbus.Core.Interfaces
{
    /// <summary>
    /// 与传输无关的响应 PDU 语义校验：期望长度、功能码一致性、字节数、回显字段。
    /// 传输层解析器（TCP/RTU）只负责"解帧"，把 PDU 交给本接口判定。
    /// </summary>
    public interface IModbusPduValidator
    {
        /// <summary>
        /// 返回响应 PDU（从功能码字节开始，不含从站号、MBAP、CRC）的期望总字节数。
        /// pduPrefix 的内容不足以确定长度时返回 -1。pduPrefix[0] 最高位为 1（异常响应）时返回 2。
        /// </summary>
        int GetExpectedPduLength(ReadOnlySpan<byte> pduPrefix, ModbusRequest request);

        /// <summary>
        /// 校验一个完整的响应 PDU 与请求一致。pdu 的长度必须不小于 GetExpectedPduLength 的返回值。
        /// 成功返回 pdu 的前 GetExpectedPduLength 个字节；异常响应返回 ErrorKind.ModbusException；
        /// 其余校验失败返回 ErrorKind.ProtocolViolation。
        /// </summary>
        ModbusResult<ReadOnlyMemory<byte>> Validate(ReadOnlyMemory<byte> pdu, ModbusRequest request);
    }
}