using Junevy.Communication.Modbus.Core.Interfaces;
using Junevy.Communication.Modbus.Core.Models;
using Junevy.Communication.Modbus.Utils;

namespace Junevy.Communication.Modbus.Extensions
{
    public static class ModbusExtensions
    {
        private static ModbusResult<T[]> ExecuteReadRequest<T>(
            IModbus modBus,
            byte slaveId,
            ushort start,
            ushort length,
            ModbusFunctionCode functionCode,
            Func<byte[], int, T[]> parser)
        {
            var result = ExecuteRawRequest(modBus, slaveId, functionCode, start, length, null);
            if (!result.IsSuccess || result.Data == null)
                return ModbusResult<T[]>.Fail(result.ErrorMessage ?? "Request failed.");

            var parsed = parser(result.Data, length);
            return ModbusResult<T[]>.Success(parsed);
        }

        private static async ValueTask<ModbusResult<T[]>> ExecuteReadRequestAsync<T>(
            IModbus modBus,
            byte slaveId,
            ushort start,
            ushort length,
            ModbusFunctionCode functionCode,
            Func<byte[], int, T[]> parser,
            CancellationToken cancellationToken = default)
        {
            var result = await ExecuteRawRequestAsync(modBus, slaveId, functionCode, start, length, null, cancellationToken);
            if (!result.IsSuccess || result.Data == null)
                return ModbusResult<T[]>.Fail(result.ErrorMessage ?? "Request failed.");

            var parsed = parser(result.Data, length);
            return ModbusResult<T[]>.Success(parsed);
        }

        private static ModbusResult<byte[]> ExecuteWriteRequest(
            IModbus modBus,
            byte slaveId,
            ushort start,
            ushort length,
            ModbusFunctionCode functionCode,
            byte[] data)
        {
            return ExecuteRawRequest(modBus, slaveId, functionCode, start, length, data);
        }

        private static ValueTask<ModbusResult<byte[]>> ExecuteWriteRequestAsync(
            IModbus modBus,
            byte slaveId,
            ushort start,
            ushort length,
            ModbusFunctionCode functionCode,
            byte[] data,
            CancellationToken cancellationToken = default)
        {
            return ExecuteRawRequestAsync(modBus, slaveId, functionCode, start, length, data, cancellationToken);
        }

        private static ModbusResult<byte[]> ExecuteRawRequest(
            IModbus modBus,
            byte slaveId,
            ModbusFunctionCode functionCode,
            ushort start = 0,
            ushort length = 0,
            byte[]? data = null)
        {
            var request = new ModbusRequest
            {
                SlaveId = slaveId,
                FunctionCode = functionCode,
                Start = start,
                Length = length,
                Data = data
            };

            var result = modBus.Request(request);
            return NormalizeRawResult(modBus.ProtocolType, result);
        }

        private static async ValueTask<ModbusResult<byte[]>> ExecuteRawRequestAsync(
            IModbus modBus,
            byte slaveId,
            ModbusFunctionCode functionCode,
            ushort start = 0,
            ushort length = 0,
            byte[]? data = null,
            CancellationToken cancellationToken = default)
        {
            var request = new ModbusRequest
            {
                SlaveId = slaveId,
                FunctionCode = functionCode,
                Start = start,
                Length = length,
                Data = data
            };

            var result = await modBus.RequestAsync(request, cancellationToken);
            return NormalizeRawResult(modBus.ProtocolType, result);
        }

        private static ModbusResult<byte[]> NormalizeRawResult(
            ModbusProtocolType protocolType,
            ModbusResult<byte[]> result)
        {
            if (!result.IsSuccess || result.Data == null || result.Data.Length == 0)
                return ModbusResult<byte[]>.Fail(result.ErrorMessage ?? "Request failed.", result.Data);

            byte[] pdu = ExtractPdu(result.Data, protocolType);
            if (pdu.Length >= 2 && (pdu[1] & 0x80) != 0)
            {
                string message = pdu.Length >= 3
                    ? $"Modbus exception response. Function=0x{pdu[1]:X2}, Code=0x{pdu[2]:X2}."
                    : $"Modbus exception response. Function=0x{pdu[1]:X2}.";
                return ModbusResult<byte[]>.Fail(message, pdu);
            }

            return ModbusResult<byte[]>.Success(pdu);
        }

        private static byte[] ExtractPdu(byte[] frame, ModbusProtocolType protocolType)
        {
            int offset = protocolType == ModbusProtocolType.TCP ? 6 : 0;
            if (frame.Length <= offset)
                return Array.Empty<byte>();

            byte[] pdu = new byte[frame.Length - offset];
            Buffer.BlockCopy(frame, offset, pdu, 0, pdu.Length);
            return pdu;
        }

        public static ModbusResult<bool[]> ReadCoils(this IModbus modBus, byte slaveId, ushort start, ushort length)
        {
            ValidateBitQuantity(length, 2000, nameof(ReadCoils));
            return ExecuteReadRequest(modBus, slaveId, start, length, ModbusFunctionCode.ReadCoils, ModbusHelper.ParseCoils);
        }

        public static ValueTask<ModbusResult<bool[]>> ReadCoilsAsync(
            this IModbus modBus,
            byte slaveId,
            ushort start,
            ushort length,
            CancellationToken cancellationToken = default)
        {
            ValidateBitQuantity(length, 2000, nameof(ReadCoilsAsync));
            return ExecuteReadRequestAsync(modBus, slaveId, start, length, ModbusFunctionCode.ReadCoils, ModbusHelper.ParseCoils, cancellationToken);
        }

        public static ModbusResult<bool[]> ReadDiscreteInputs(this IModbus modBus, byte slaveId, ushort start, ushort length)
        {
            ValidateBitQuantity(length, 2000, nameof(ReadDiscreteInputs));
            return ExecuteReadRequest(modBus, slaveId, start, length, ModbusFunctionCode.ReadDiscreteInputs, ModbusHelper.ParseCoils);
        }

        public static ValueTask<ModbusResult<bool[]>> ReadDiscreteInputsAsync(
            this IModbus modBus,
            byte slaveId,
            ushort start,
            ushort length,
            CancellationToken cancellationToken = default)
        {
            ValidateBitQuantity(length, 2000, nameof(ReadDiscreteInputsAsync));
            return ExecuteReadRequestAsync(modBus, slaveId, start, length, ModbusFunctionCode.ReadDiscreteInputs, ModbusHelper.ParseCoils, cancellationToken);
        }

        public static ModbusResult<ushort[]> ReadHoldingRegisters(this IModbus modBus, byte slaveId, ushort start, ushort length)
        {
            ValidateRegisterQuantity(length, 125, nameof(ReadHoldingRegisters));
            return ExecuteReadRequest(modBus, slaveId, start, length, ModbusFunctionCode.ReadHoldingRegisters, ModbusHelper.ParseRegisters);
        }

        public static ValueTask<ModbusResult<ushort[]>> ReadHoldingRegistersAsync(
            this IModbus modBus,
            byte slaveId,
            ushort start,
            ushort length,
            CancellationToken cancellationToken = default)
        {
            ValidateRegisterQuantity(length, 125, nameof(ReadHoldingRegistersAsync));
            return ExecuteReadRequestAsync(modBus, slaveId, start, length, ModbusFunctionCode.ReadHoldingRegisters, ModbusHelper.ParseRegisters, cancellationToken);
        }

        public static ModbusResult<ushort[]> ReadInputRegisters(this IModbus modBus, byte slaveId, ushort start, ushort length)
        {
            ValidateRegisterQuantity(length, 125, nameof(ReadInputRegisters));
            return ExecuteReadRequest(modBus, slaveId, start, length, ModbusFunctionCode.ReadInputRegisters, ModbusHelper.ParseRegisters);
        }

        public static ValueTask<ModbusResult<ushort[]>> ReadInputRegistersAsync(
            this IModbus modBus,
            byte slaveId,
            ushort start,
            ushort length,
            CancellationToken cancellationToken = default)
        {
            ValidateRegisterQuantity(length, 125, nameof(ReadInputRegistersAsync));
            return ExecuteReadRequestAsync(modBus, slaveId, start, length, ModbusFunctionCode.ReadInputRegisters, ModbusHelper.ParseRegisters, cancellationToken);
        }

        public static ModbusResult<byte[]> WriteSingleCoil(this IModbus modBus, byte slaveId, ushort start, bool value)
        {
            byte[] data = new byte[] { (byte)(value ? 0xFF : 0x00), 0x00 };
            return ExecuteWriteRequest(modBus, slaveId, start, 1, ModbusFunctionCode.WriteCoil, data);
        }

        public static ValueTask<ModbusResult<byte[]>> WriteSingleCoilAsync(
            this IModbus modBus,
            byte slaveId,
            ushort start,
            bool value,
            CancellationToken cancellationToken = default)
        {
            byte[] data = new byte[] { (byte)(value ? 0xFF : 0x00), 0x00 };
            return ExecuteWriteRequestAsync(modBus, slaveId, start, 1, ModbusFunctionCode.WriteCoil, data, cancellationToken);
        }

        public static ModbusResult<byte[]> WriteSingleRegister(this IModbus modBus, byte slaveId, ushort start, ushort value)
        {
            return ExecuteWriteRequest(modBus, slaveId, start, 1, ModbusFunctionCode.WriteHoldingRegister, value.ToBigEndian());
        }

        public static ValueTask<ModbusResult<byte[]>> WriteSingleRegisterAsync(
            this IModbus modBus,
            byte slaveId,
            ushort start,
            ushort value,
            CancellationToken cancellationToken = default)
        {
            return ExecuteWriteRequestAsync(modBus, slaveId, start, 1, ModbusFunctionCode.WriteHoldingRegister, value.ToBigEndian(), cancellationToken);
        }

        public static ModbusResult<byte[]> WriteMultipleCoils(this IModbus modBus, byte slaveId, ushort start, bool[] values)
        {
            byte[] data = PackCoils(values);
            return ExecuteWriteRequest(modBus, slaveId, start, (ushort)values.Length, ModbusFunctionCode.WriteMultipleCoils, data);
        }

        public static ValueTask<ModbusResult<byte[]>> WriteMultipleCoilsAsync(
            this IModbus modBus,
            byte slaveId,
            ushort start,
            bool[] values,
            CancellationToken cancellationToken = default)
        {
            byte[] data = PackCoils(values);
            return ExecuteWriteRequestAsync(modBus, slaveId, start, (ushort)values.Length, ModbusFunctionCode.WriteMultipleCoils, data, cancellationToken);
        }

        public static ModbusResult<byte[]> WriteMultipleRegisters(this IModbus modBus, byte slaveId, ushort start, ushort[] values)
        {
            ValidateWriteRegisters(values, 123, nameof(WriteMultipleRegisters));
            return ExecuteWriteRequest(modBus, slaveId, start, (ushort)values.Length, ModbusFunctionCode.WriteMultipleHoldingRegisters, values.ToBigEndianByteArray());
        }

        public static ValueTask<ModbusResult<byte[]>> WriteMultipleRegistersAsync(
            this IModbus modBus,
            byte slaveId,
            ushort start,
            ushort[] values,
            CancellationToken cancellationToken = default)
        {
            ValidateWriteRegisters(values, 123, nameof(WriteMultipleRegistersAsync));
            return ExecuteWriteRequestAsync(modBus, slaveId, start, (ushort)values.Length, ModbusFunctionCode.WriteMultipleHoldingRegisters, values.ToBigEndianByteArray(), cancellationToken);
        }

        public static ModbusResult<byte> ReadExceptionStatus(this IModbus modBus, byte slaveId)
        {
            var result = ExecuteRawRequest(modBus, slaveId, ModbusFunctionCode.ReadExceptionStatus);
            return result.IsSuccess && result.Data != null && result.Data.Length >= 3
                ? ModbusResult<byte>.Success(result.Data[2])
                : ModbusResult<byte>.Fail(result.ErrorMessage ?? "Read exception status failed.");
        }

        public static async ValueTask<ModbusResult<byte>> ReadExceptionStatusAsync(
            this IModbus modBus,
            byte slaveId,
            CancellationToken cancellationToken = default)
        {
            var result = await ExecuteRawRequestAsync(modBus, slaveId, ModbusFunctionCode.ReadExceptionStatus, cancellationToken: cancellationToken);
            return result.IsSuccess && result.Data != null && result.Data.Length >= 3
                ? ModbusResult<byte>.Success(result.Data[2])
                : ModbusResult<byte>.Fail(result.ErrorMessage ?? "Read exception status failed.");
        }

        public static ModbusResult<byte[]> Diagnostics(this IModbus modBus, byte slaveId, ushort subFunction, ushort data)
        {
            return Diagnostics(modBus, slaveId, subFunction, data.ToBigEndian());
        }

        public static ModbusResult<byte[]> Diagnostics(this IModbus modBus, byte slaveId, ushort subFunction, byte[] data)
        {
            var payload = BuildDiagnosticsData(subFunction, data);
            return ExecuteRawRequest(modBus, slaveId, ModbusFunctionCode.Diagnostics, data: payload);
        }

        public static ValueTask<ModbusResult<byte[]>> DiagnosticsAsync(
            this IModbus modBus,
            byte slaveId,
            ushort subFunction,
            ushort data,
            CancellationToken cancellationToken = default)
        {
            return DiagnosticsAsync(modBus, slaveId, subFunction, data.ToBigEndian(), cancellationToken);
        }

        public static ValueTask<ModbusResult<byte[]>> DiagnosticsAsync(
            this IModbus modBus,
            byte slaveId,
            ushort subFunction,
            byte[] data,
            CancellationToken cancellationToken = default)
        {
            var payload = BuildDiagnosticsData(subFunction, data);
            return ExecuteRawRequestAsync(modBus, slaveId, ModbusFunctionCode.Diagnostics, data: payload, cancellationToken: cancellationToken);
        }

        public static ModbusResult<ModbusCommEventCounter> GetCommEventCounter(this IModbus modBus, byte slaveId)
        {
            var result = ExecuteRawRequest(modBus, slaveId, ModbusFunctionCode.GetCommEventCounter);
            return ParseCommEventCounter(result);
        }

        public static async ValueTask<ModbusResult<ModbusCommEventCounter>> GetCommEventCounterAsync(
            this IModbus modBus,
            byte slaveId,
            CancellationToken cancellationToken = default)
        {
            var result = await ExecuteRawRequestAsync(modBus, slaveId, ModbusFunctionCode.GetCommEventCounter, cancellationToken: cancellationToken);
            return ParseCommEventCounter(result);
        }

        public static ModbusResult<ModbusCommEventLog> GetCommEventLog(this IModbus modBus, byte slaveId)
        {
            var result = ExecuteRawRequest(modBus, slaveId, ModbusFunctionCode.GetCommEventLog);
            return ParseCommEventLog(result);
        }

        public static async ValueTask<ModbusResult<ModbusCommEventLog>> GetCommEventLogAsync(
            this IModbus modBus,
            byte slaveId,
            CancellationToken cancellationToken = default)
        {
            var result = await ExecuteRawRequestAsync(modBus, slaveId, ModbusFunctionCode.GetCommEventLog, cancellationToken: cancellationToken);
            return ParseCommEventLog(result);
        }

        public static ModbusResult<byte[]> ReportServerId(this IModbus modBus, byte slaveId)
        {
            var result = ExecuteRawRequest(modBus, slaveId, ModbusFunctionCode.ReportServerId);
            return ExtractByteCountPayload(result, "Report server id failed.");
        }

        public static async ValueTask<ModbusResult<byte[]>> ReportServerIdAsync(
            this IModbus modBus,
            byte slaveId,
            CancellationToken cancellationToken = default)
        {
            var result = await ExecuteRawRequestAsync(modBus, slaveId, ModbusFunctionCode.ReportServerId, cancellationToken: cancellationToken);
            return ExtractByteCountPayload(result, "Report server id failed.");
        }

        public static ModbusResult<byte[]> MaskWriteRegister(this IModbus modBus, byte slaveId, ushort start, ushort andMask, ushort orMask)
        {
            byte[] data = Combine(andMask.ToBigEndian(), orMask.ToBigEndian());
            return ExecuteWriteRequest(modBus, slaveId, start, 1, ModbusFunctionCode.MaskWriteRegister, data);
        }

        public static ValueTask<ModbusResult<byte[]>> MaskWriteRegisterAsync(
            this IModbus modBus,
            byte slaveId,
            ushort start,
            ushort andMask,
            ushort orMask,
            CancellationToken cancellationToken = default)
        {
            byte[] data = Combine(andMask.ToBigEndian(), orMask.ToBigEndian());
            return ExecuteWriteRequestAsync(modBus, slaveId, start, 1, ModbusFunctionCode.MaskWriteRegister, data, cancellationToken);
        }

        public static ModbusResult<ushort[]> ReadWriteMultipleRegisters(
            this IModbus modBus,
            byte slaveId,
            ushort readStart,
            ushort readLength,
            ushort writeStart,
            ushort[] writeValues)
        {
            ValidateRegisterQuantity(readLength, 125, nameof(ReadWriteMultipleRegisters));
            ValidateWriteRegisters(writeValues, 121, nameof(ReadWriteMultipleRegisters));

            byte[] data = BuildReadWriteMultipleRegistersData(readStart, readLength, writeStart, writeValues);
            var result = ExecuteRawRequest(modBus, slaveId, ModbusFunctionCode.ReadWriteMultipleRegisters, readStart, readLength, data);
            return result.IsSuccess && result.Data != null
                ? ModbusResult<ushort[]>.Success(ModbusHelper.ParseRegisters(result.Data, readLength))
                : ModbusResult<ushort[]>.Fail(result.ErrorMessage ?? "Read/write multiple registers failed.");
        }

        public static async ValueTask<ModbusResult<ushort[]>> ReadWriteMultipleRegistersAsync(
            this IModbus modBus,
            byte slaveId,
            ushort readStart,
            ushort readLength,
            ushort writeStart,
            ushort[] writeValues,
            CancellationToken cancellationToken = default)
        {
            ValidateRegisterQuantity(readLength, 125, nameof(ReadWriteMultipleRegistersAsync));
            ValidateWriteRegisters(writeValues, 121, nameof(ReadWriteMultipleRegistersAsync));

            byte[] data = BuildReadWriteMultipleRegistersData(readStart, readLength, writeStart, writeValues);
            var result = await ExecuteRawRequestAsync(modBus, slaveId, ModbusFunctionCode.ReadWriteMultipleRegisters, readStart, readLength, data, cancellationToken);
            return result.IsSuccess && result.Data != null
                ? ModbusResult<ushort[]>.Success(ModbusHelper.ParseRegisters(result.Data, readLength))
                : ModbusResult<ushort[]>.Fail(result.ErrorMessage ?? "Read/write multiple registers failed.");
        }

        private static ModbusResult<ModbusCommEventCounter> ParseCommEventCounter(ModbusResult<byte[]> result)
        {
            if (!result.IsSuccess || result.Data == null || result.Data.Length < 6)
                return ModbusResult<ModbusCommEventCounter>.Fail(result.ErrorMessage ?? "Get communication event counter failed.");

            return ModbusResult<ModbusCommEventCounter>.Success(new ModbusCommEventCounter
            {
                Status = BinaryExtensions.ToUshort(result.Data[3], result.Data[2]),
                EventCount = BinaryExtensions.ToUshort(result.Data[5], result.Data[4])
            });
        }

        private static ModbusResult<ModbusCommEventLog> ParseCommEventLog(ModbusResult<byte[]> result)
        {
            if (!result.IsSuccess || result.Data == null || result.Data.Length < 9)
                return ModbusResult<ModbusCommEventLog>.Fail(result.ErrorMessage ?? "Get communication event log failed.");

            int eventBytes = Math.Max(0, result.Data[2] - 6);
            byte[] events = new byte[Math.Min(eventBytes, result.Data.Length - 9)];
            if (events.Length > 0)
                Buffer.BlockCopy(result.Data, 9, events, 0, events.Length);

            return ModbusResult<ModbusCommEventLog>.Success(new ModbusCommEventLog
            {
                Status = BinaryExtensions.ToUshort(result.Data[4], result.Data[3]),
                EventCount = BinaryExtensions.ToUshort(result.Data[6], result.Data[5]),
                MessageCount = BinaryExtensions.ToUshort(result.Data[8], result.Data[7]),
                Events = events
            });
        }

        private static ModbusResult<byte[]> ExtractByteCountPayload(ModbusResult<byte[]> result, string errorMessage)
        {
            if (!result.IsSuccess || result.Data == null || result.Data.Length < 3)
                return ModbusResult<byte[]>.Fail(result.ErrorMessage ?? errorMessage);

            int count = Math.Min(result.Data[2], result.Data.Length - 3);
            byte[] payload = new byte[count];
            if (count > 0)
                Buffer.BlockCopy(result.Data, 3, payload, 0, count);

            return ModbusResult<byte[]>.Success(payload);
        }

        private static byte[] BuildDiagnosticsData(ushort subFunction, byte[] data)
        {
            if (data == null || data.Length == 0 || data.Length > 250)
                throw new ModbusException(ModbusErrorCode.InvalidQuantity, "Diagnostics data length must be between 1 and 250.");

            byte[] sub = subFunction.ToBigEndian();
            return Combine(sub, data);
        }

        private static byte[] BuildReadWriteMultipleRegistersData(
            ushort readStart,
            ushort readLength,
            ushort writeStart,
            ushort[] writeValues)
        {
            byte[] writeData = writeValues.ToBigEndianByteArray();
            byte[] data = new byte[9 + writeData.Length];
            Buffer.BlockCopy(readStart.ToBigEndian(), 0, data, 0, 2);
            Buffer.BlockCopy(readLength.ToBigEndian(), 0, data, 2, 2);
            Buffer.BlockCopy(writeStart.ToBigEndian(), 0, data, 4, 2);
            Buffer.BlockCopy(((ushort)writeValues.Length).ToBigEndian(), 0, data, 6, 2);
            data[8] = (byte)writeData.Length;
            Buffer.BlockCopy(writeData, 0, data, 9, writeData.Length);
            return data;
        }

        private static byte[] PackCoils(bool[] values)
        {
            if (values == null || values.Length == 0 || values.Length > 1968)
                throw new ModbusException(ModbusErrorCode.InvalidQuantity, "Coil quantity must be between 1 and 1968.");

            byte[] data = new byte[(values.Length + 7) / 8];
            for (int i = 0; i < values.Length; i++)
            {
                if (values[i])
                    data[i / 8] |= (byte)(1 << (i % 8));
            }

            return data;
        }

        private static void ValidateBitQuantity(ushort quantity, ushort max, string name)
        {
            if (quantity == 0 || quantity > max)
                throw new ModbusException(ModbusErrorCode.InvalidQuantity, $"[{name}] Quantity must be between 1 and {max}.");
        }

        private static void ValidateRegisterQuantity(ushort quantity, ushort max, string name)
        {
            if (quantity == 0 || quantity > max)
                throw new ModbusException(ModbusErrorCode.InvalidQuantity, $"[{name}] Quantity must be between 1 and {max}.");
        }

        private static void ValidateWriteRegisters(ushort[] values, ushort max, string name)
        {
            if (values == null || values.Length == 0 || values.Length > max)
                throw new ModbusException(ModbusErrorCode.InvalidQuantity, $"[{name}] Register quantity must be between 1 and {max}.");
        }

        private static byte[] Combine(byte[] first, byte[] second)
        {
            byte[] result = new byte[first.Length + second.Length];
            Buffer.BlockCopy(first, 0, result, 0, first.Length);
            Buffer.BlockCopy(second, 0, result, first.Length, second.Length);
            return result;
        }
    }
}
