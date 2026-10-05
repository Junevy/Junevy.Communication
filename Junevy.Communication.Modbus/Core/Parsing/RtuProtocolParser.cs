using System.Buffers.Binary;
using Junevy.Communication.Modbus.Core.Interfaces;
using Junevy.Communication.Modbus.Core.Models;
using Junevy.Communication.Modbus.Extensions;
using Junevy.Communication.Modbus.Utils;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using System.Diagnostics;

namespace Junevy.Communication.Modbus.Core.Parsing
{
    /// <summary>
    /// Parses Modbus RTU response frames. Implements <see cref="IResponseParser"/> directly
    /// so it can be injected into <see cref="RTU.ModbusRtuClient"/> as its response parser.
    /// </summary>
    public sealed class RtuProtocolParser : IResponseParser
    {
        private const int RtuMinFrameLength = 5;

        private readonly ILogger<RtuProtocolParser> logger;
        private readonly ModbusPduVerifier verifier;
        private readonly Stopwatch stopwatch = Stopwatch.StartNew();
        private long lastTimestamp;

        public RtuProtocolParser(
            ILogger<RtuProtocolParser>? logger = null,
            ModbusPduVerifier? verifier = null)
        {
            this.logger = logger ?? NullLogger<RtuProtocolParser>.Instance;
            this.verifier = verifier ?? new ModbusPduVerifier();
        }

        public ModbusResult<ReadOnlyMemory<byte>> ParseResponse(ReadOnlyMemory<byte> response, ModbusRequest request)
        {
            // Common validation
            if (response.Length == 0)
                return ModbusResult<ReadOnlyMemory<byte>>.Fail(" [RtuParser] The response is empty.", ModbusErrorKind.ProtocolViolation);

            if (!ModbusHelper.CheckRequest(request))
                return ModbusResult<ReadOnlyMemory<byte>>.Fail(" [RtuParser] The request is invalid.", ModbusErrorKind.ProtocolViolation);

            // RTU-specific: scan with Span offset (zero-copy)
            var span = response.Span;
            int offset = 0;

            while (offset + RtuMinFrameLength <= span.Length)
            {
                var remainingMemory = response.Slice(offset);
                var remaining = remainingMemory.Span;

                byte id = remaining[0];
                byte funcCode = remaining[1];

                if (id != request.SlaveId)
                {
                    logger.LogWarning(" [RtuParser] Slave ID mismatch. Expected {Expected}, actual {Actual}. Skipping byte.", request.SlaveId, id);
                    offset++;
                    continue;
                }

                // Exception response
                if (funcCode == (byte)((byte)request.FunctionCode | 0x80))
                {
                    var (exceptionResult, shouldRetry) = HandleRtuException(remainingMemory);
                    if (!shouldRetry)
                        return exceptionResult;
                    offset++;
                    continue;
                }

                var commonResult = TryHandleRtuCommonFunction(remainingMemory, request);
                if (commonResult.Handled)
                {
                    if (commonResult.Retry)
                    {
                        offset++;
                        continue;
                    }

                    return commonResult.Result;
                }

                var category = verifier.CategorizeFunctionCode(request.FunctionCode);
                if (category == ModbusPduVerifier.FunctionCodeCategory.Unknown)
                {
                    logger.LogWarning(" [RtuParser] Unknown function code. Skipping byte.");
                    offset++;
                    continue;
                }

                var data = request.Data ?? [];

                // 0x16 Mask Write Register — special handling (response is an echo with 4 data bytes)
                if (request.FunctionCode == ModbusFunctionCode.MaskWriteRegister)
                {
                    var maskResult = HandleRtuMaskWrite(remainingMemory, request.StartAddress, data);
                    if (maskResult.Retry)
                    {
                        offset++;
                        continue;
                    }
                    return maskResult.Result;
                }

                var (payloadResult, retry) = category switch
                {
                    ModbusPduVerifier.FunctionCodeCategory.Read =>
                        HandleRtuRead(remainingMemory, request.FunctionCode, request.Quantity),

                    ModbusPduVerifier.FunctionCodeCategory.WriteSingle =>
                        HandleRtuWriteSingle(remainingMemory, request.StartAddress, data),

                    ModbusPduVerifier.FunctionCodeCategory.WriteMulti =>
                        HandleRtuWriteMulti(remainingMemory, request.StartAddress, request.Quantity),

                    _ => DefaultUnmatched(remaining)
                };

                if (retry)
                {
                    offset++;
                    continue;
                }

                return payloadResult;
            }

            logger.LogError(" [RtuParser] Failed to match response in buffer.");
            return ModbusResult<ReadOnlyMemory<byte>>.Fail(" [RtuParser] Failed to match response.", ModbusErrorKind.ProtocolViolation, response);
        }

        private (ModbusResult<ReadOnlyMemory<byte>> Result, bool Retry) HandleRtuException(ReadOnlyMemory<byte> response)
        {
            const int exceptionLength = 5;
            var span = response.Span;

            if (exceptionLength > span.Length)
            {
                logger.LogWarning(" [HandleRtuException] Exception response too short: {Actual} < {Expected}.", span.Length, exceptionLength);
                return (ModbusResult<ReadOnlyMemory<byte>>.Fail(
                    $"Exception response too short. Expected {exceptionLength}, actual {span.Length}.", ModbusErrorKind.ProtocolViolation, response), false);
            }

            var candidate = span.Slice(0, exceptionLength);
            if (Crc16Helper.VerifyCrc(candidate))
            {
                logger.Rx("SerialPort", candidate, stopwatch, ref lastTimestamp);
                byte exceptionCode = span[2];
                return (ModbusResult<ReadOnlyMemory<byte>>.Fail(
                    $"Modbus exception response. Function=0x{span[1]:X2}, Code=0x{exceptionCode:X2}.",
                    ModbusErrorKind.ModbusException,
                    response.Slice(0, exceptionLength)), false);
            }

            logger.LogWarning(" [HandleRtuException] CRC verification failed. Skipping byte.");
            return (ModbusResult<ReadOnlyMemory<byte>>.Fail(" [HandleRtuException] CRC verification failed.", ModbusErrorKind.ProtocolViolation, response.Slice(0, exceptionLength)), true);
        }

        private (ModbusResult<ReadOnlyMemory<byte>> Result, bool Retry) HandleRtuRead(
            ReadOnlyMemory<byte> response, ModbusFunctionCode functionCode, ushort length)
        {
            var span = response.Span;
            int byteCount = span[2];
            int expectedLength = 3 + byteCount + 2;

            if (span.Length < expectedLength)
            {
                logger.LogWarning(" [HandleRtuRead] Response too short: {Actual} < {Expected}.", span.Length, expectedLength);
                return (ModbusResult<ReadOnlyMemory<byte>>.Fail(
                    $"Response too short. Expected {expectedLength}, actual {span.Length}.", ModbusErrorKind.ProtocolViolation, response), false);
            }

            var candidate = span.Slice(0, expectedLength);

            if (!verifier.VerifyReadPdu(candidate, functionCode, length))
            {
                logger.LogWarning(" [HandleRtuRead] PDU verification failed.");
                return (ModbusResult<ReadOnlyMemory<byte>>.Fail(" [HandleRtuRead] PDU verification failed.", ModbusErrorKind.ProtocolViolation, response), true);
            }

            if (Crc16Helper.VerifyCrc(candidate))
            {
                logger.Rx("SerialPort", candidate, stopwatch, ref lastTimestamp);
                return (ModbusResult<ReadOnlyMemory<byte>>.Success(response.Slice(0, expectedLength)), false);
            }

            logger.LogWarning(" [HandleRtuRead] CRC verification failed. Skipping byte.");
            return (ModbusResult<ReadOnlyMemory<byte>>.Fail(" [HandleRtuRead] CRC verification failed.", ModbusErrorKind.ProtocolViolation, response.Slice(0, expectedLength)), true);
        }

        private (ModbusResult<ReadOnlyMemory<byte>> Result, bool Retry) HandleRtuWriteSingle(
            ReadOnlyMemory<byte> response, ushort startAddr, byte[] data)
        {
            var span = response.Span;
            if (data == null || data.Length == 0 || span.Length < 8)
            {
                logger.LogWarning(" [HandleRtuWriteSingle] Invalid data or response length.");
                return (ModbusResult<ReadOnlyMemory<byte>>.Fail(
                    " [HandleRtuWriteSingle] Invalid data or response length.", ModbusErrorKind.ProtocolViolation, response), false);
            }

            var candidate = span.Slice(0, 8);
            if (!verifier.VerifySingleWritePdu(candidate, startAddr, data))
            {
                logger.LogWarning(" [HandleRtuWriteSingle] PDU verification failed.");
                return (ModbusResult<ReadOnlyMemory<byte>>.Fail(
                    " [HandleRtuWriteSingle] PDU verification failed.", ModbusErrorKind.ProtocolViolation, response), true);
            }

            if (Crc16Helper.VerifyCrc(candidate))
            {
                logger.Rx("SerialPort", candidate, stopwatch, ref lastTimestamp);
                return (ModbusResult<ReadOnlyMemory<byte>>.Success(response.Slice(0, 8)), false);
            }

            logger.LogWarning(" [HandleRtuWriteSingle] CRC verification failed. Skipping byte.");
            return (ModbusResult<ReadOnlyMemory<byte>>.Fail(" [HandleRtuWriteSingle] CRC verification failed.", ModbusErrorKind.ProtocolViolation, response.Slice(0, 8)), true);
        }

        private (ModbusResult<ReadOnlyMemory<byte>> Result, bool Retry) HandleRtuMaskWrite(
            ReadOnlyMemory<byte> response, ushort startAddr, byte[] data)
        {
            var span = response.Span;
            // Data = [AndHi, AndLo, OrHi, OrLo]
            if (data == null || data.Length != 4 || span.Length < 10)
            {
                logger.LogWarning(" [HandleRtuMaskWrite] Invalid data or response length.");
                return (ModbusResult<ReadOnlyMemory<byte>>.Fail(
                    " [HandleRtuMaskWrite] Invalid data or response length.", ModbusErrorKind.ProtocolViolation, response), false);
            }

            var andMask = BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(0, 2));
            var orMask = BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(2, 2));

            var candidate = span.Slice(0, 10); // SlaveId + FuncCode + Start(2) + And(2) + Or(2) + CRC(2)

            if (!verifier.VerifyMaskWritePdu(candidate, startAddr, andMask, orMask))
            {
                logger.LogWarning(" [HandleRtuMaskWrite] PDU verification failed.");
                return (ModbusResult<ReadOnlyMemory<byte>>.Fail(
                    " [HandleRtuMaskWrite] PDU verification failed.", ModbusErrorKind.ProtocolViolation, response), true);
            }

            if (Crc16Helper.VerifyCrc(candidate))
            {
                logger.Rx("SerialPort", candidate, stopwatch, ref lastTimestamp);
                return (ModbusResult<ReadOnlyMemory<byte>>.Success(response.Slice(0, 10)), false);
            }

            logger.LogWarning(" [HandleRtuMaskWrite] CRC verification failed. Skipping byte.");
            return (ModbusResult<ReadOnlyMemory<byte>>.Fail(" [HandleRtuMaskWrite] CRC verification failed.", ModbusErrorKind.ProtocolViolation, response.Slice(0, 10)), true);
        }

        private (ModbusResult<ReadOnlyMemory<byte>> Result, bool Retry) HandleRtuWriteMulti(
            ReadOnlyMemory<byte> response, ushort startAddr, ushort length)
        {
            var span = response.Span;
            if (span.Length < 8)
            {
                logger.LogWarning(" [HandleRtuWriteMulti] Response too short: {Actual} < 8.", span.Length);
                return (ModbusResult<ReadOnlyMemory<byte>>.Fail(
                    " [HandleRtuWriteMulti] Response too short.", ModbusErrorKind.ProtocolViolation, response), false);
            }

            var candidate = span.Slice(0, 8);
            if (!verifier.VerifyMultiWritePdu(candidate, startAddr, length))
            {
                logger.LogWarning(" [HandleRtuWriteMulti] PDU verification failed.");
                return (ModbusResult<ReadOnlyMemory<byte>>.Fail(
                    " [HandleRtuWriteMulti] PDU verification failed.", ModbusErrorKind.ProtocolViolation, response), true);
            }

            if (Crc16Helper.VerifyCrc(candidate))
            {
                logger.Rx("SerialPort", candidate, stopwatch, ref lastTimestamp);
                return (ModbusResult<ReadOnlyMemory<byte>>.Success(response.Slice(0, 8)), false);
            }

            logger.LogWarning(" [HandleRtuWriteMulti] CRC verification failed. Skipping byte.");
            return (ModbusResult<ReadOnlyMemory<byte>>.Fail(" [HandleRtuWriteMulti] CRC verification failed.", ModbusErrorKind.ProtocolViolation, response.Slice(0, 8)), true);
        }

        private (bool Handled, ModbusResult<ReadOnlyMemory<byte>> Result, bool Retry) TryHandleRtuCommonFunction(
            ReadOnlyMemory<byte> response,
            ModbusRequest request)
        {
            int expectedLength = request.FunctionCode switch
            {
                ModbusFunctionCode.ReadExceptionStatus => 5,
                ModbusFunctionCode.Diagnostics => 4 + (request.Data?.Length ?? 0),
                ModbusFunctionCode.GetCommEventCounter => 8,
                ModbusFunctionCode.GetCommEventLog => response.Length >= 3 ? 3 + response.Span[2] + 2 : 0,
                ModbusFunctionCode.ReportServerId => response.Length >= 3 ? 3 + response.Span[2] + 2 : 0,
                _ => 0
            };

            if (expectedLength == 0)
                return (false, ModbusResult<ReadOnlyMemory<byte>>.Fail(string.Empty), false);

            if (response.Length < expectedLength)
            {
                return (true, ModbusResult<ReadOnlyMemory<byte>>.Fail(
                    $" [RtuParser] Response too short. Expected {expectedLength}, actual {response.Length}.", ModbusErrorKind.ProtocolViolation, response), false);
            }

            var candidate = response.Slice(0, expectedLength);
            if (Crc16Helper.VerifyCrc(candidate.Span))
                return (true, ModbusResult<ReadOnlyMemory<byte>>.Success(candidate), false);

            return (true, ModbusResult<ReadOnlyMemory<byte>>.Fail(" [RtuParser] CRC verification failed.", ModbusErrorKind.ProtocolViolation, candidate), true);
        }

        private static (ModbusResult<ReadOnlyMemory<byte>> Result, bool Retry) DefaultUnmatched(ReadOnlySpan<byte> response)
        {
            return (ModbusResult<ReadOnlyMemory<byte>>.Fail(" [DefaultUnmatched] Unknown function code.", ModbusErrorKind.ProtocolViolation, response.ToArray()), false);
        }
    }
}
