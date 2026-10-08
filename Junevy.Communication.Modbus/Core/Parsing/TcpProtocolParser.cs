using Junevy.Communication.Modbus.Core.Interfaces;
using Junevy.Communication.Modbus.Core.Models;
using Junevy.Communication.Modbus.Utils;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using System.Buffers.Binary;

namespace Junevy.Communication.Modbus.Core.Parsing
{
    /// <summary>
    /// Parses the MBAP framing of Modbus TCP responses and delegates PDU semantics to
    /// <see cref="IModbusPduValidator"/>. Injected into <c>ModbusTcpClient</c> as its response parser.
    /// RX logging is done by the client, not here.
    /// </summary>
public sealed class TcpProtocolParser : IResponseParser
    {
    private const int TcpPduOffset = 6;

        private readonly ILogger<TcpProtocolParser> logger;
        private readonly IModbusPduValidator validator;

        public TcpProtocolParser(
            IModbusPduValidator? validator = null,
            ILogger<TcpProtocolParser>? logger = null)
        {
            this.logger = logger ?? NullLogger<TcpProtocolParser>.Instance;
            this.validator = validator ?? new ModbusPduValidator();
        }

        public ModbusResult<ReadOnlyMemory<byte>> ParseResponse(ReadOnlyMemory<byte> response, ModbusRequest request)
        {
            if (response.Length == 0)
                return ModbusResult<ReadOnlyMemory<byte>>.Fail(" [TcpParser] The response is empty.", ModbusErrorKind.ProtocolViolation);

            if (!ModbusHelper.CheckRequest(request))
                return ModbusResult<ReadOnlyMemory<byte>>.Fail(" [TcpParser] The request is invalid.", ModbusErrorKind.ProtocolViolation);

            // MBAP 头 7 字节 + 最少 2 字节 PDU
            if (response.Length < 9)
            {
                logger.LogWarning(" [TcpParser] Response too short: {Length} bytes.", response.Length);
                return ModbusResult<ReadOnlyMemory<byte>>.Fail(" [TcpParser] Response too short.", ModbusErrorKind.ProtocolViolation, response);
            }

            var span = response.Span;

            ushort protocolId = BinaryPrimitives.ReadUInt16BigEndian(span.Slice(2, 2));
            ushort frameLength = BinaryPrimitives.ReadUInt16BigEndian(span.Slice(4, 2));
            byte unitId = span[6];
            ushort transactionId = BinaryPrimitives.ReadUInt16BigEndian(span.Slice(0, 2));
            ushort expectedTransactionId = request.TransactionId;

            if (protocolId != 0x00)
            {
                logger.LogWarning(" [TcpParser] Invalid protocol ID: {ProtocolId}.", protocolId);
                return ModbusResult<ReadOnlyMemory<byte>>.Fail($"Invalid protocol ID: {protocolId}.", ModbusErrorKind.ProtocolViolation, response);
            }

            if (transactionId != expectedTransactionId)
            {
                logger.LogWarning(" [TcpParser] Transaction ID mismatch. Expected {Expected}, actual {Actual}.", expectedTransactionId, transactionId);
                return ModbusResult<ReadOnlyMemory<byte>>.Fail(
                    $"Transaction ID mismatch. Expected {expectedTransactionId}, actual {transactionId}.", ModbusErrorKind.ProtocolViolation, response);
            }

            if (unitId != request.SlaveId)
            {
                logger.LogWarning(" [TcpParser] Slave ID mismatch. Expected {Expected}, actual {Actual}.", request.SlaveId, unitId);
                return ModbusResult<ReadOnlyMemory<byte>>.Fail(
                    $"Slave ID mismatch. Expected {request.SlaveId}, actual {unitId}.", ModbusErrorKind.ProtocolViolation, response);
            }

            int totalLength = TcpPduOffset + frameLength;
            if (response.Length < totalLength)
            {
                logger.LogWarning(" [TcpParser] Invalid response length. Expected {Expected}, actual {Actual}.", totalLength, response.Length);
                return ModbusResult<ReadOnlyMemory<byte>>.Fail($"Invalid response length. Expected {totalLength}, actual {response.Length}.", ModbusErrorKind.ProtocolViolation, response);
            }

            var pdu = response.Slice(TcpPduOffset + 1, totalLength - TcpPduOffset - 1);
            if (pdu.Length < 1)
            {
                logger.LogWarning(" [TcpParser] Response too short: no PDU.");
                return ModbusResult<ReadOnlyMemory<byte>>.Fail(" [TcpParser] Response too short.", ModbusErrorKind.ProtocolViolation, response);
            }

            int n = validator.GetExpectedPduLength(pdu.Span, request);
            if (n < 0 || pdu.Length < n)
            {
                logger.LogWarning(" [TcpParser] Response too short: expected PDU length {Expected}, got {Actual}.", n, pdu.Length);
                return ModbusResult<ReadOnlyMemory<byte>>.Fail(" [TcpParser] Response too short.", ModbusErrorKind.ProtocolViolation, response);
            }

            var v = validator.Validate(pdu, request);
            if (v.ErrorKind == ModbusErrorKind.ModbusException)
                return ModbusResult<ReadOnlyMemory<byte>>.Fail(v.ErrorMessage!, ModbusErrorKind.ModbusException, response.Slice(0, totalLength));

            if (!v.IsSuccess)
                return ModbusResult<ReadOnlyMemory<byte>>.Fail(v.ErrorMessage!, ModbusErrorKind.ProtocolViolation, response);

            return ModbusResult<ReadOnlyMemory<byte>>.Success(response.Slice(0, TcpPduOffset + 1 + n));
        }
    }
}