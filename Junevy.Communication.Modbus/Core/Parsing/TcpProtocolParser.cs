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
    /// Parses Modbus TCP response frames. Implements <see cref="IResponseParser"/> directly
    /// so it can be injected into <see cref="TCP.ModbusTCP"/> as its response parser.
    /// </summary>
    public sealed class TcpProtocolParser : IResponseParser
    {
        private const int TcpMinFrameLength = 9;
        private const int TcpPduOffset = 6;

        private readonly ILogger<TcpProtocolParser> logger;
        private readonly ModbusPduVerifier verifier;
        private readonly Stopwatch stopwatch = Stopwatch.StartNew();
        private long lastTimestamp;

        public TcpProtocolParser(
            ILogger<TcpProtocolParser>? logger = null,
            ModbusPduVerifier? verifier = null)
        {
            this.logger = logger ?? NullLogger<TcpProtocolParser>.Instance;
            this.verifier = verifier ?? new ModbusPduVerifier();
        }

        public ModbusResult<ReadOnlyMemory<byte>> ParseResponse(ReadOnlyMemory<byte> response, ModbusRequest request)
        {
            // Common validation
            if (response.Length == 0)
                return ModbusResult<ReadOnlyMemory<byte>>.Fail(" [TcpParser] The response is empty.");

            if (!ModbusHelper.CheckRequest(request))
                return ModbusResult<ReadOnlyMemory<byte>>.Fail(" [TcpParser] The request is invalid.");

            // TCP-specific validation
            if (response.Length < TcpMinFrameLength)
            {
                logger.LogWarning(" [TcpParser] Response too short: {Length} bytes.", response.Length);
                return ModbusResult<ReadOnlyMemory<byte>>.Fail(" [TcpParser] Response too short.", response);
            }

            var span = response.Span;

            ushort protocolId = BinaryExtensions.ToUshort(span[3], span[2]);
            ushort frameLength = BinaryExtensions.ToUshort(span[5], span[4]);
            byte unitId = span[6];
            byte funcCode = span[7];
            ushort transactionId = BinaryExtensions.ToUshort(span[1], span[0]);
            ushort expectedTransactionId = request.TransactionId;

            if (protocolId != 0x00)
            {
                logger.LogWarning(" [TcpParser] Invalid protocol ID: {ProtocolId}.", protocolId);
                return ModbusResult<ReadOnlyMemory<byte>>.Fail($"Invalid protocol ID: {protocolId}.", response);
            }

            if (transactionId != expectedTransactionId)
            {
                logger.LogWarning(" [TcpParser] Transaction ID mismatch. Expected {Expected}, actual {Actual}.", expectedTransactionId, transactionId);
                return ModbusResult<ReadOnlyMemory<byte>>.Fail(
                    $"Transaction ID mismatch. Expected {expectedTransactionId}, actual {transactionId}.", response);
            }

            if (unitId != request.SlaveId)
            {
                logger.LogWarning(" [TcpParser] Slave ID mismatch. Expected {Expected}, actual {Actual}.", request.SlaveId, unitId);
                return ModbusResult<ReadOnlyMemory<byte>>.Fail(
                    $"Slave ID mismatch. Expected {request.SlaveId}, actual {unitId}.", response);
            }

            int totalLength = TcpPduOffset + frameLength;
            if (response.Length < totalLength)
            {
                logger.LogWarning(" [TcpParser] Invalid response length. Expected {Expected}, actual {Actual}.", totalLength, response.Length);
                return ModbusResult<ReadOnlyMemory<byte>>.Fail($"Invalid response length. Expected {totalLength}, actual {response.Length}.", response);
            }

            // Exception response
            if (funcCode == (byte)((byte)request.FunctionCode | 0x80))
            {
                if (totalLength < TcpPduOffset + 3)
                {
                    logger.LogWarning(" [TcpParser] Exception response too short: {Length} bytes.", totalLength);
                    return ModbusResult<ReadOnlyMemory<byte>>.Fail(" [TcpParser] Exception response too short.", response);
                }

                byte exceptionCode = span[TcpPduOffset + 2];
                logger.LogWarning(" [TcpParser] Modbus exception. Function=0x{Function:X2}, Code=0x{Code:X2}.", funcCode, exceptionCode);
                return ModbusResult<ReadOnlyMemory<byte>>.Fail(
                    $"Modbus exception response. Function=0x{funcCode:X2}, Code=0x{exceptionCode:X2}.",
                    response.Slice(0, totalLength));
            }

            var data = request.Data ?? [];

            // 0x16 Mask Write Register — special handling (response is an echo with 4 data bytes)
            if (request.FunctionCode == ModbusFunctionCode.MaskWriteRegister)
                return HandleTcpMaskWrite(response.Slice(0, totalLength), request.Start, data);

            var commonResult = TryHandleTcpCommonFunction(response.Slice(0, totalLength), request);
            if (commonResult.Handled)
                return commonResult.Result;

            return verifier.CategorizeFunctionCode(request.FunctionCode) switch
            {
                ModbusPduVerifier.FunctionCodeCategory.Read =>
                    HandleTcpRead(response.Slice(0, totalLength), request.FunctionCode, request.Length),

                ModbusPduVerifier.FunctionCodeCategory.WriteSingle =>
                    HandleTcpWriteSingle(response.Slice(0, totalLength), request.Start, data),

                ModbusPduVerifier.FunctionCodeCategory.WriteMulti =>
                    HandleTcpWriteMulti(response.Slice(0, totalLength), request.Start, request.Length),

                _ => DefaultUnmatched(response)
            };
        }

        private ModbusResult<ReadOnlyMemory<byte>> HandleTcpMaskWrite(ReadOnlyMemory<byte> response,
            ushort startAddr, byte[] data)
        {
            // Data = [AndHi, AndLo, OrHi, OrLo]
            if (data == null || data.Length != 4 || response.Length < TcpPduOffset + 8)
            {
                logger.LogWarning(" [HandleTcpMaskWrite] Invalid data or response length.");
                return ModbusResult<ReadOnlyMemory<byte>>.Fail(" [HandleTcpMaskWrite] Invalid data or response length.", response);
            }

            var andMask = BinaryExtensions.ToUshort(data[1], data[0]);
            var orMask = BinaryExtensions.ToUshort(data[3], data[2]);

            var pduSpan = response.Span.Slice(TcpPduOffset, 8); // UnitId + FuncCode + Start(2) + And(2) + Or(2)

            if (!verifier.VerifyMaskWritePdu(pduSpan, startAddr, andMask, orMask))
            {
                logger.LogWarning(" [HandleTcpMaskWrite] PDU verification failed.");
                return ModbusResult<ReadOnlyMemory<byte>>.Fail(" [HandleTcpMaskWrite] PDU verification failed.", response);
            }

            return ModbusResult<ReadOnlyMemory<byte>>.Success(response.Slice(0, TcpPduOffset + 8));
        }

        private ModbusResult<ReadOnlyMemory<byte>> HandleTcpRead(ReadOnlyMemory<byte> response,
            ModbusFunctionCode functionCode, ushort length)
        {
            if (response.Length < TcpPduOffset + 3)
            {
                logger.LogWarning(" [HandleTcpRead] Response too short: {Actual} < {Expected}.", response.Length, TcpPduOffset + 3);
                return ModbusResult<ReadOnlyMemory<byte>>.Fail(" [HandleTcpRead] Response too short.", response);
            }

            int byteCount = response.Span[TcpPduOffset + 2];
            int pduDataLength = 3 + byteCount;
            int expectedLength = TcpPduOffset + pduDataLength;

            if (response.Length < expectedLength)
            {
                logger.LogWarning(" [HandleTcpRead] Response too short: {Actual} < {Expected}.", response.Length, expectedLength);
                return ModbusResult<ReadOnlyMemory<byte>>.Fail(" [HandleTcpRead] Response too short.", response);
            }

            var cutFrame = response.Slice(0, expectedLength);
            var pduSpan = cutFrame.Span.Slice(TcpPduOffset, pduDataLength);

            if (!verifier.VerifyReadPdu(pduSpan, functionCode, length))
            {
                logger.LogWarning(" [HandleTcpRead] PDU verification failed.");
                return ModbusResult<ReadOnlyMemory<byte>>.Fail(" [HandleTcpRead] PDU verification failed.", response);
            }

            return ModbusResult<ReadOnlyMemory<byte>>.Success(cutFrame);
        }

        private ModbusResult<ReadOnlyMemory<byte>> HandleTcpWriteSingle(ReadOnlyMemory<byte> response,
            ushort startAddr, byte[] data)
        {
            if (data == null || data.Length == 0 || response.Length < TcpPduOffset + 6)
            {
                logger.LogWarning(" [HandleTcpWriteSingle] Invalid data or response length.");
                return ModbusResult<ReadOnlyMemory<byte>>.Fail(" [HandleTcpWriteSingle] Invalid data or response length.", response);
            }

            var pduSpan = response.Span.Slice(TcpPduOffset, 6);

            if (!verifier.VerifySingleWritePdu(pduSpan, startAddr, data))
            {
                logger.LogWarning(" [HandleTcpWriteSingle] PDU verification failed.");
                return ModbusResult<ReadOnlyMemory<byte>>.Fail(" [HandleTcpWriteSingle] PDU verification failed.", response);
            }

            return ModbusResult<ReadOnlyMemory<byte>>.Success(response.Slice(0, TcpPduOffset + 6));
        }

        private ModbusResult<ReadOnlyMemory<byte>> HandleTcpWriteMulti(ReadOnlyMemory<byte> response,
            ushort startAddr, ushort length)
        {
            if (response.Length < TcpPduOffset + 6)
            {
                logger.LogWarning(" [HandleTcpWriteMulti] Response too short: {Actual} < {Expected}.", response.Length, TcpPduOffset + 6);
                return ModbusResult<ReadOnlyMemory<byte>>.Fail(" [HandleTcpWriteMulti] Response too short.", response);
            }

            var pduSpan = response.Span.Slice(TcpPduOffset, 6);

            if (!verifier.VerifyMultiWritePdu(pduSpan, startAddr, length))
            {
                logger.LogWarning(" [HandleTcpWriteMulti] PDU verification failed.");
                return ModbusResult<ReadOnlyMemory<byte>>.Fail(" [HandleTcpWriteMulti] PDU verification failed.", response);
            }

            return ModbusResult<ReadOnlyMemory<byte>>.Success(response.Slice(0, TcpPduOffset + 6));
        }

        private ModbusResult<ReadOnlyMemory<byte>> DefaultUnmatched(ReadOnlyMemory<byte> response)
        {
            logger.Rx("TCP", response.Span, stopwatch, ref lastTimestamp);
            return ModbusResult<ReadOnlyMemory<byte>>.Fail(" [TcpParser] Function code cannot be matched.", response);
        }

        private static (bool Handled, ModbusResult<ReadOnlyMemory<byte>> Result) TryHandleTcpCommonFunction(
            ReadOnlyMemory<byte> response,
            ModbusRequest request)
        {
            bool handled = request.FunctionCode == ModbusFunctionCode.ReadExceptionStatus
                || request.FunctionCode == ModbusFunctionCode.Diagnostics
                || request.FunctionCode == ModbusFunctionCode.GetCommEventCounter
                || request.FunctionCode == ModbusFunctionCode.GetCommEventLog
                || request.FunctionCode == ModbusFunctionCode.ReportServerId;

            if (!handled)
                return (false, ModbusResult<ReadOnlyMemory<byte>>.Fail(string.Empty));

            return (true, ModbusResult<ReadOnlyMemory<byte>>.Success(response));
        }
    }
}
