using Junevy.Communication.Modbus.Core.Models;
using Junevy.Communication.Modbus.Utils;
using System.Buffers.Binary;

namespace Junevy.Communication.Modbus.Extensions
{
    /// <summary>
    /// 请求数据打包、参数校验与响应映射。仅供本命名空间内的三个公开扩展类使用。
    /// </summary>
    internal static class ModbusPayloadCodec
    {
        /// <summary>数据不足时返回 false 而不抛异常的解析委托。</summary>
        internal delegate bool TryParse<T>(byte[] response, int length, out T[] values);

        // 方法组到泛型委托的转换无法推断 T，用显式实例化的委托字段承接
        internal static readonly TryParse<bool> TryParseCoils = ModbusHelper.TryParseCoils;
        internal static readonly TryParse<ushort> TryParseRegisters = ModbusHelper.TryParseRegisters;

        private const string ShortDataMessage = "Response data is shorter than the requested quantity.";

        // ————————————————— 请求数据打包 —————————————————

        internal static byte[] PackCoils(bool[] values)
        {
            if (values == null || values.Length == 0 || values.Length > 1968)
                throw new ArgumentException("Coil quantity must be between 1 and 1968.", nameof(values));

            byte[] data = new byte[(values.Length + 7) / 8];
            for (int i = 0; i < values.Length; i++)
            {
                if (values[i])
                    data[i / 8] |= (byte)(1 << (i % 8));
            }

            return data;
        }

        internal static byte[] BuildDiagnosticsData(ushort subFunction, byte[] data)
        {
            if (data == null || data.Length == 0 || data.Length > 250)
                throw new ArgumentException("Diagnostics data length must be between 1 and 250.", nameof(data));

            byte[] sub = subFunction.ToBigEndian();
            return Combine(sub, data);
        }

        internal static byte[] BuildReadWriteMultipleRegistersData(
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

        internal static byte[] Combine(byte[] first, byte[] second)
        {
            byte[] result = new byte[first.Length + second.Length];
            Buffer.BlockCopy(first, 0, result, 0, first.Length);
            Buffer.BlockCopy(second, 0, result, first.Length, second.Length);
            return result;
        }

        // ————————————————— 参数校验 —————————————————

        internal static void ValidateBitQuantity(ushort quantity, ushort max, string name)
        {
            if (quantity == 0 || quantity > max)
                throw new ArgumentException($"[{name}] Quantity must be between 1 and {max}.");
        }

        internal static void ValidateRegisterQuantity(ushort quantity, ushort max, string name)
        {
            if (quantity == 0 || quantity > max)
                throw new ArgumentException($"[{name}] Quantity must be between 1 and {max}.");
        }

        internal static void ValidateWriteRegisters(ushort[] values, ushort max, string name)
        {
            if (values == null || values.Length == 0 || values.Length > max)
                throw new ArgumentException($"[{name}] Register quantity must be between 1 and {max}.");
        }

        // ————————————————— 响应映射 —————————————————

        internal static ModbusResult<T[]> MapRead<T>(
            ModbusResult<byte[]> result,
            int length,
            TryParse<T> parser)
        {
            if (!result.IsSuccess || result.Data == null)
                return ModbusResult<T[]>.Fail(result.ErrorMessage ?? "Request failed.", result.ErrorKind);

            if (!parser(result.Data, length, out var parsed))
                return ModbusResult<T[]>.Fail(ShortDataMessage, ModbusErrorKind.ProtocolViolation);

            return ModbusResult<T[]>.Success(parsed);
        }

        internal static ModbusResult<ModbusCommEventCounter> MapCommEventCounter(ModbusResult<byte[]> result)
        {
            if (!result.IsSuccess || result.Data == null)
                return ModbusResult<ModbusCommEventCounter>.Fail(result.ErrorMessage ?? "Get communication event counter failed.", result.ErrorKind);

            if (result.Data.Length < 6)
                return ModbusResult<ModbusCommEventCounter>.Fail("Communication event counter response is shorter than expected.", ModbusErrorKind.ProtocolViolation);

            return ModbusResult<ModbusCommEventCounter>.Success(new ModbusCommEventCounter
            {
                Status = BinaryPrimitives.ReadUInt16BigEndian(result.Data.AsSpan(2, 2)),
                EventCount = BinaryPrimitives.ReadUInt16BigEndian(result.Data.AsSpan(4, 2))
            });
        }

        internal static ModbusResult<ModbusCommEventLog> MapCommEventLog(ModbusResult<byte[]> result)
        {
            if (!result.IsSuccess || result.Data == null)
                return ModbusResult<ModbusCommEventLog>.Fail(result.ErrorMessage ?? "Get communication event log failed.", result.ErrorKind);

            if (result.Data.Length < 9)
                return ModbusResult<ModbusCommEventLog>.Fail("Communication event log response is shorter than expected.", ModbusErrorKind.ProtocolViolation);

            int eventBytes = Math.Max(0, result.Data[2] - 6);
            byte[] events = new byte[Math.Min(eventBytes, result.Data.Length - 9)];
            if (events.Length > 0)
                Buffer.BlockCopy(result.Data, 9, events, 0, events.Length);

            return ModbusResult<ModbusCommEventLog>.Success(new ModbusCommEventLog
            {
                Status = BinaryPrimitives.ReadUInt16BigEndian(result.Data.AsSpan(3, 2)),
                EventCount = BinaryPrimitives.ReadUInt16BigEndian(result.Data.AsSpan(5, 2)),
                MessageCount = BinaryPrimitives.ReadUInt16BigEndian(result.Data.AsSpan(7, 2)),
                Events = events
            });
        }

        internal static ModbusResult<byte[]> MapByteCountPayload(ModbusResult<byte[]> result, string errorMessage)
        {
            if (!result.IsSuccess || result.Data == null)
                return ModbusResult<byte[]>.Fail(result.ErrorMessage ?? errorMessage, result.ErrorKind);

            // 帧头声明的事务字节数（Data[2]）超过实际可用数据 = 响应被截断
            if (result.Data.Length < 3 || result.Data[2] > result.Data.Length - 3)
                return ModbusResult<byte[]>.Fail("Response data is shorter than the declared byte count.", ModbusErrorKind.ProtocolViolation);

            int count = Math.Min(result.Data[2], result.Data.Length - 3);
            byte[] payload = new byte[count];
            if (count > 0)
                Buffer.BlockCopy(result.Data, 3, payload, 0, count);

            return ModbusResult<byte[]>.Success(payload);
        }

        internal static ModbusResult<byte> MapExceptionStatus(ModbusResult<byte[]> result)
        {
            if (!result.IsSuccess || result.Data == null)
                return ModbusResult<byte>.Fail(result.ErrorMessage ?? "Read exception status failed.", result.ErrorKind);

            if (result.Data.Length < 3)
                return ModbusResult<byte>.Fail("Read exception status response is shorter than expected.", ModbusErrorKind.ProtocolViolation);

            return ModbusResult<byte>.Success(result.Data[2]);
        }
    }
}