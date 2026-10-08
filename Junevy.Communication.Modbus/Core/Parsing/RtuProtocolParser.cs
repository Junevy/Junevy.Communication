using Junevy.Communication.Modbus.Core.Interfaces;
using Junevy.Communication.Modbus.Core.Models;
using Junevy.Communication.Modbus.Utils;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Junevy.Communication.Modbus.Core.Parsing
{
    /// <summary>
    /// Scans an RTU byte stream for a frame addressed to the requested slave, verifies its CRC,
    /// and delegates PDU semantics to <see cref="IModbusPduValidator"/>.
    /// Injected into <c>ModbusRtuClient</c> as its response parser.
    /// RX logging is done by the client, not here.
    /// </summary>
    public sealed class RtuProtocolParser : IResponseParser
    {
        private const int RtuMinFrameLength = 5;
        private const int RtuCrcLength = 2;

        private readonly ILogger<RtuProtocolParser> logger;
        private readonly IModbusPduValidator validator;

        public RtuProtocolParser(
            IModbusPduValidator? validator = null,
            ILogger<RtuProtocolParser>? logger = null)
        {
            this.logger = logger ?? NullLogger<RtuProtocolParser>.Instance;
            this.validator = validator ?? new ModbusPduValidator();
        }

        public ModbusResult<ReadOnlyMemory<byte>> ParseResponse(ReadOnlyMemory<byte> response, ModbusRequest request)
        {
            if (response.Length == 0)
                return ModbusResult<ReadOnlyMemory<byte>>.Fail(" [RtuParser] The response is empty.", ModbusErrorKind.ProtocolViolation);

            if (!ModbusHelper.CheckRequest(request))
                return ModbusResult<ReadOnlyMemory<byte>>.Fail(" [RtuParser] The request is invalid.", ModbusErrorKind.ProtocolViolation);

            int offset = 0;
            while (offset + RtuMinFrameLength <= response.Length)
            {
                var frame = response.Slice(offset);
                var span = frame.Span;

                if (span[0] != request.SlaveId)
                {
                    logger.LogWarning(" [RtuParser] Slave ID mismatch. Expected {Expected}, actual {Actual}. Skipping byte.", request.SlaveId, span[0]);
                    offset++;
                    continue;
                }

                int n = validator.GetExpectedPduLength(span.Slice(1), request);
                if (n < 0)
                {
                    // 前缀不足以确定长度：不跳过，调用方据此继续读取更多字节
                    logger.LogWarning(" [RtuParser] Response too short: expected PDU length is undetermined.");
                    return ModbusResult<ReadOnlyMemory<byte>>.Fail(" [RtuParser] Response too short.", ModbusErrorKind.ProtocolViolation, frame);
                }

                int total = 1 + n + RtuCrcLength;
                if (frame.Length < total)
                {
                    // 帧尚未收全：不跳过，调用方据此继续读取更多字节
                    logger.LogWarning(" [RtuParser] Response too short. Expected {Expected}, actual {Actual}.", total, frame.Length);
                    return ModbusResult<ReadOnlyMemory<byte>>.Fail(
                        $" [RtuParser] Response too short. Expected {total}, actual {frame.Length}.", ModbusErrorKind.ProtocolViolation, frame);
                }

                var candidate = frame.Slice(0, total);
                var v = validator.Validate(candidate.Slice(1, n), request);

                // 非异常且校验失败 → 视为噪声，继续向后扫描
                if (!v.IsSuccess && v.ErrorKind != ModbusErrorKind.ModbusException)
                {
                    logger.LogWarning(" [RtuParser] PDU validation failed. Skipping byte.");
                    offset++;
                    continue;
                }

                if (!Crc16Helper.VerifyCrc(candidate.Span))
                {
                    logger.LogWarning(" [RtuParser] CRC verification failed. Skipping byte.");
                    offset++;
                    continue;
                }

                if (v.IsSuccess)
                    return ModbusResult<ReadOnlyMemory<byte>>.Success(candidate);

                return ModbusResult<ReadOnlyMemory<byte>>.Fail(v.ErrorMessage!, ModbusErrorKind.ModbusException, candidate);
            }

            logger.LogError(" [RtuParser] Failed to match response in buffer.");
            return ModbusResult<ReadOnlyMemory<byte>>.Fail(" [RtuParser] Failed to match response.", ModbusErrorKind.ProtocolViolation, response);
        }
    }
}