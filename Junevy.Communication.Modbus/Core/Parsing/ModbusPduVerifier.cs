using System.Buffers.Binary;
using Junevy.Communication.Modbus.Core.Models;
using Junevy.Communication.Modbus.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Junevy.Communication.Modbus.Core.Parsing
{
    /// <summary>
    /// Shared PDU verification and function-code categorization logic.
    /// Used by both TCP and RTU protocol parsers — extracted to avoid duplication.
    /// Registered as a singleton in the DI container.
    /// </summary>
    public class ModbusPduVerifier
    {
        public enum FunctionCodeCategory
        {
            Read,
            WriteSingle,
            WriteMulti,
            Unknown
        }

        private readonly ILogger<ModbusPduVerifier> logger;

        public ModbusPduVerifier(ILogger<ModbusPduVerifier>? logger = null)
        {
            this.logger = logger ?? NullLogger<ModbusPduVerifier>.Instance;
        }

        internal FunctionCodeCategory CategorizeFunctionCode(ModbusFunctionCode functionCode)
        {
            if (functionCode >= ModbusFunctionCode.ReadCoils && functionCode <= ModbusFunctionCode.ReadInputRegisters)
                return FunctionCodeCategory.Read;
            if (functionCode >= ModbusFunctionCode.WriteCoil && functionCode <= ModbusFunctionCode.WriteHoldingRegister)
                return FunctionCodeCategory.WriteSingle;
            if (functionCode >= ModbusFunctionCode.WriteMultipleCoils && functionCode <= ModbusFunctionCode.WriteMultipleHoldingRegisters)
                return FunctionCodeCategory.WriteMulti;
            // 0x16 MaskWriteRegister → echoes request (WriteSingle path)
            if (functionCode == ModbusFunctionCode.MaskWriteRegister)
                return FunctionCodeCategory.WriteSingle;
            // 0x17 ReadWriteMultipleRegisters → returns read data (Read path)
            if (functionCode == ModbusFunctionCode.ReadWriteMultipleRegisters)
                return FunctionCodeCategory.Read;
            return FunctionCodeCategory.Unknown;
        }

        internal bool VerifyReadPdu(ReadOnlySpan<byte> frame, ModbusFunctionCode functionCode, ushort length)
        {
            int expectedByteCount;
            byte byteCount = frame[2];

            if (functionCode == ModbusFunctionCode.ReadHoldingRegisters
                || functionCode == ModbusFunctionCode.ReadInputRegisters
                || functionCode == ModbusFunctionCode.ReadWriteMultipleRegisters)
                expectedByteCount = length * 2;
            else
                expectedByteCount = (length + 7) / 8;

            if (byteCount == expectedByteCount)
            {
                logger.LogDebug(" [VerifyReadPdu] Read successful.");
                return true;
            }

            logger.LogWarning(" [VerifyReadPdu] Byte count mismatch. Expected {Expected}, actual {Actual}.", expectedByteCount, byteCount);
            return false;
        }

        internal bool VerifySingleWritePdu(ReadOnlySpan<byte> frame, ushort startAddress, byte[] data)
        {
            var startAdr = BinaryPrimitives.ReadUInt16BigEndian(frame.Slice(2, 2));
            if (startAdr != startAddress)
            {
                logger.LogWarning(" [VerifySingleWritePdu] Start address mismatch. Expected {Expected}, actual {Actual}.", startAddress, startAdr);
                return false;
            }

            if (data.Length != 2)
            {
                logger.LogWarning(" [VerifySingleWritePdu] Invalid data length: {Length}.", data.Length);
                return false;
            }

            var frameSpan = frame.Slice(4, 2);
            return frameSpan[0] == data[0] && frameSpan[1] == data[1];
        }

        internal bool VerifyMultiWritePdu(ReadOnlySpan<byte> frame, ushort startAddress, ushort length)
        {
            var start = BinaryPrimitives.ReadUInt16BigEndian(frame.Slice(2, 2));
            if (start != startAddress)
            {
                logger.LogWarning(" [VerifyMultiWritePdu] Start address mismatch. Expected {Expected}, actual {Actual}.", startAddress, start);
                return false;
            }

            var dataLength = BinaryPrimitives.ReadUInt16BigEndian(frame.Slice(4, 2));
            if (dataLength != length)
            {
                logger.LogWarning(" [VerifyMultiWritePdu] Length mismatch. Expected {Expected}, actual {Actual}.", length, dataLength);
                return false;
            }

            logger.LogDebug(" [VerifyMultiWritePdu] Write multiple successful.");
            return true;
        }

        /// <summary>
        /// Verifies a Mask Write Register (0x16) response PDU.
        /// The response echoes the request: [FuncCode, Start(2), AndMask(2), OrMask(2)].
        /// </summary>
        internal bool VerifyMaskWritePdu(ReadOnlySpan<byte> frame, ushort startAddress, ushort andMask, ushort orMask)
        {
            // frame layout (starting from UnitId): [UnitId, 0x16, StartHi, StartLo, AndHi, AndLo, OrHi, OrLo]
            int expectedLength = 8; // UnitId + FuncCode + Start(2) + AndMask(2) + OrMask(2)
            if (frame.Length < expectedLength)
            {
                logger.LogWarning(" [VerifyMaskWritePdu] PDU too short: {Actual} < {Expected}.", frame.Length, expectedLength);
                return false;
            }

            var actualStart = BinaryPrimitives.ReadUInt16BigEndian(frame.Slice(2, 2));
            if (actualStart != startAddress)
            {
                logger.LogWarning(" [VerifyMaskWritePdu] Start address mismatch. Expected {Expected}, actual {Actual}.", startAddress, actualStart);
                return false;
            }

            var actualAnd = BinaryPrimitives.ReadUInt16BigEndian(frame.Slice(4, 2));
            if (actualAnd != andMask)
            {
                logger.LogWarning(" [VerifyMaskWritePdu] AND mask mismatch. Expected {Expected}, actual {Actual}.", andMask, actualAnd);
                return false;
            }

            var actualOr = BinaryPrimitives.ReadUInt16BigEndian(frame.Slice(6, 2));
            if (actualOr != orMask)
            {
                logger.LogWarning(" [VerifyMaskWritePdu] OR mask mismatch. Expected {Expected}, actual {Actual}.", orMask, actualOr);
                return false;
            }

            logger.LogDebug(" [VerifyMaskWritePdu] Mask write successful.");
            return true;
        }
    }
}
