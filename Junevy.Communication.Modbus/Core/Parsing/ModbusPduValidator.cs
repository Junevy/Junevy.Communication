using Junevy.Communication.Modbus.Core.Interfaces;
using Junevy.Communication.Modbus.Core.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using System.Buffers.Binary;

namespace Junevy.Communication.Modbus.Core.Parsing
{
    /// <summary>
    /// <see cref="IModbusPduValidator"/> 的默认实现：与传输无关的 PDU 语义校验。
    /// 不记录 RX 日志（RX 日志由客户端 ModbusTransportBase.LogRx 统一记录）。
    /// </summary>
    public sealed class ModbusPduValidator : IModbusPduValidator
    {
        private readonly ILogger<ModbusPduValidator> logger;

        public ModbusPduValidator(ILogger<ModbusPduValidator>? logger = null)
        {
            this.logger = logger ?? NullLogger<ModbusPduValidator>.Instance;
        }

        /// <inheritdoc />
        public int GetExpectedPduLength(ReadOnlySpan<byte> pduPrefix, ModbusRequest request)
        {
            if (pduPrefix.Length == 0)
                return -1;

            // 异常响应固定 2 字节：功能码 + 异常码
            if ((pduPrefix[0] & 0x80) != 0)
                return 2;

            switch (request.FunctionCode)
            {
                // 读类：功能码 + 字节数 + 数据；字节数在 pduPrefix[1]
                case ModbusFunctionCode.ReadCoils:
                case ModbusFunctionCode.ReadDiscreteInputs:
                case ModbusFunctionCode.ReadHoldingRegisters:
                case ModbusFunctionCode.ReadInputRegisters:
                case ModbusFunctionCode.ReadWriteMultipleRegisters:
                case ModbusFunctionCode.GetCommEventLog:
                case ModbusFunctionCode.ReportServerId:
                    if (pduPrefix.Length < 2)
                        return -1;
                    return 2 + pduPrefix[1];

                case ModbusFunctionCode.WriteCoil:
                case ModbusFunctionCode.WriteHoldingRegister:
                case ModbusFunctionCode.WriteMultipleCoils:
                case ModbusFunctionCode.WriteMultipleHoldingRegisters:
                    return 5;

                case ModbusFunctionCode.MaskWriteRegister:
                    return 7;

                case ModbusFunctionCode.ReadExceptionStatus:
                    return 2;

                case ModbusFunctionCode.Diagnostics:
                    return 1 + (request.Data?.Length ?? 0);

                case ModbusFunctionCode.GetCommEventCounter:
                    return 5;

                default:
                    return -1;
            }
        }

        /// <inheritdoc />
        public ModbusResult<ReadOnlyMemory<byte>> Validate(ReadOnlyMemory<byte> pdu, ModbusRequest request)
        {
            var span = pdu.Span;

            if (pdu.Length < 1)
                return Violation(" [PduValidator] The response PDU is empty.");

            // 1) 异常响应：功能码必须等于请求功能码 | 0x80
            if ((span[0] & 0x80) != 0)
            {
                if (span[0] != (byte)((byte)request.FunctionCode | 0x80))
                {
                    return Violation(
                        $" [PduValidator] Function code mismatch. Expected 0x{(byte)((byte)request.FunctionCode | 0x80):X2}, actual 0x{span[0]:X2}.");
                }

                if (pdu.Length < 2)
                    return Violation(" [PduValidator] Exception response PDU is too short.");

                return ModbusResult<ReadOnlyMemory<byte>>.Fail(
                    $"Modbus exception response. Function=0x{span[0]:X2}, Code=0x{span[1]:X2}.",
                    ModbusErrorKind.ModbusException,
                    pdu.Slice(0, 2));
            }

            // 2) D1（本计划唯一的行为变更）：正常响应功能码必须等于请求功能码
            if (span[0] != (byte)request.FunctionCode)
            {
                return Violation(
                    $" [PduValidator] Function code mismatch. Expected 0x{(byte)request.FunctionCode:X2}, actual 0x{span[0]:X2}.");
            }

            int expectedLength = GetExpectedPduLength(span, request);
            if (expectedLength < 0 || pdu.Length < expectedLength)
                return Violation($" [PduValidator] Response PDU is too short. Expected {expectedLength}, actual {pdu.Length}.");

            var trimmed = pdu.Slice(0, expectedLength);
            var content = trimmed.Span;

            switch (request.FunctionCode)
            {
                case ModbusFunctionCode.ReadCoils:
                case ModbusFunctionCode.ReadDiscreteInputs:
                    if (content[1] != (byte)((request.Quantity + 7) / 8))
                        return Violation($" [PduValidator] Byte count mismatch. Expected {(request.Quantity + 7) / 8}, actual {content[1]}.");
                    break;

                case ModbusFunctionCode.ReadHoldingRegisters:
                case ModbusFunctionCode.ReadInputRegisters:
                case ModbusFunctionCode.ReadWriteMultipleRegisters:
                    if (content[1] != (byte)(request.Quantity * 2))
                        return Violation($" [PduValidator] Byte count mismatch. Expected {request.Quantity * 2}, actual {content[1]}.");
                    break;

                case ModbusFunctionCode.WriteCoil:
                case ModbusFunctionCode.WriteHoldingRegister:
                    if (BinaryPrimitives.ReadUInt16BigEndian(content.Slice(1, 2)) != request.StartAddress)
                        return Violation($" [PduValidator] Start address mismatch. Expected {request.StartAddress}, actual {BinaryPrimitives.ReadUInt16BigEndian(content.Slice(1, 2))}.");
                    if (request.Data == null || request.Data.Length < 2
                        || content[3] != request.Data[0] || content[4] != request.Data[1])
                        return Violation(" [PduValidator] Echoed value mismatch.");
                    break;

                case ModbusFunctionCode.WriteMultipleCoils:
                case ModbusFunctionCode.WriteMultipleHoldingRegisters:
                    if (BinaryPrimitives.ReadUInt16BigEndian(content.Slice(1, 2)) != request.StartAddress)
                        return Violation($" [PduValidator] Start address mismatch. Expected {request.StartAddress}, actual {BinaryPrimitives.ReadUInt16BigEndian(content.Slice(1, 2))}.");
                    if (BinaryPrimitives.ReadUInt16BigEndian(content.Slice(3, 2)) != request.Quantity)
                        return Violation($" [PduValidator] Length mismatch. Expected {request.Quantity}, actual {BinaryPrimitives.ReadUInt16BigEndian(content.Slice(3, 2))}.");
                    break;

                case ModbusFunctionCode.MaskWriteRegister:
                    if (BinaryPrimitives.ReadUInt16BigEndian(content.Slice(1, 2)) != request.StartAddress)
                        return Violation($" [PduValidator] Start address mismatch. Expected {request.StartAddress}, actual {BinaryPrimitives.ReadUInt16BigEndian(content.Slice(1, 2))}.");
                    if (request.Data == null || request.Data.Length < 4
                        || content[3] != request.Data[0] || content[4] != request.Data[1]
                        || content[5] != request.Data[2] || content[6] != request.Data[3])
                        return Violation(" [PduValidator] Echoed mask mismatch.");
                    break;

                // 0x07、0x08、0x0B、0x0C、0x11 不做内容校验
            }

            logger.LogDebug(" [PduValidator] PDU validation successful for function 0x{Function:X2}.", (byte)request.FunctionCode);
            return ModbusResult<ReadOnlyMemory<byte>>.Success(trimmed);
        }

        private ModbusResult<ReadOnlyMemory<byte>> Violation(string message)
        {
            logger.LogWarning("{Message}", message);
            return ModbusResult<ReadOnlyMemory<byte>>.Fail(message, ModbusErrorKind.ProtocolViolation);
        }
    }
}