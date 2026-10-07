using Junevy.Communication.Modbus.Core.Interfaces;
using Junevy.Communication.Modbus.Core.Framing;
using Junevy.Communication.Modbus.Core.Models;
using Junevy.Communication.Modbus.Extensions;

namespace Junevy.Communication.Modbus.Utils
{
    public static class ModbusHelper
    {
        private static readonly IModbusFrameBuilder FrameBuilder = new ModbusFrameBuilder();

        /// <summary>
        /// Validates a Modbus request frame.
        /// </summary>
        public static bool CheckRequest(ModbusRequest request)
        {
            if (request is null || !Enum.IsDefined(typeof(ModbusFunctionCode), request.FunctionCode))
                return false;

            return request.FunctionCode switch
            {
                ModbusFunctionCode.ReadCoils
                    or ModbusFunctionCode.ReadDiscreteInputs =>
                    request.Quantity is >= 1 and <= 2000,

                ModbusFunctionCode.ReadHoldingRegisters
                    or ModbusFunctionCode.ReadInputRegisters =>
                    request.Quantity is >= 1 and <= 125,

                ModbusFunctionCode.WriteCoil
                    or ModbusFunctionCode.WriteHoldingRegister =>
                    request.Data is { Length: 2 },

                ModbusFunctionCode.ReadExceptionStatus
                    or ModbusFunctionCode.GetCommEventCounter
                    or ModbusFunctionCode.GetCommEventLog
                    or ModbusFunctionCode.ReportServerId =>
                    true,

                ModbusFunctionCode.Diagnostics =>
                    request.Data is not null && request.Data.Length >= 3 && request.Data.Length <= 252,

                ModbusFunctionCode.WriteMultipleCoils =>
                    request.Quantity is >= 1 and <= 1968
                    && request.Data is not null
                    && request.Data.Length == (request.Quantity + 7) / 8,

                ModbusFunctionCode.WriteMultipleHoldingRegisters =>
                    request.Quantity is >= 1 and <= 123
                    && request.Data is not null
                    && request.Data.Length == request.Quantity * 2,

                ModbusFunctionCode.MaskWriteRegister =>
                    request.Data is { Length: 4 },

                ModbusFunctionCode.ReadWriteMultipleRegisters =>
                    IsValidReadWriteMultipleRegistersRequest(request.Data),

                _ => false
            };
        }

        /// <summary>
        /// Builds a Modbus request frame from a request object.
        /// Compatibility entry point: the protocol the user explicitly set on the
        /// request is honored; this API never modifies the request object.
        /// </summary>
        /// <exception cref="ArgumentException">Thrown when the request is invalid.</exception>
        public static byte[] BuildRequestFrame(ModbusRequest request)
            => FrameBuilder.BuildRequestFrame(request, request.ProtocolType);

        /// <summary>
        /// Parses coil values from a Modbus response frame.
        /// </summary>
        public static bool[] ParseCoils(byte[] response, int length)
        {
            if (response == null)
                throw new ArgumentNullException(nameof(response), "The response data cannot be null.");

            if (length <= 0)
                throw new ArgumentException("Length must be greater than 0.", nameof(length));

            int expectedByteCount = (length + 7) / 8;
            if (response.Length < 3 + expectedByteCount)
                throw new ArgumentException("The response data is not enough for the requested length.", nameof(response));

            bool[] result = new bool[length];
            var start = 3;

            for (int i = 0; i < length; i++)
            {
                var byteIndex = i / 8;
                var bitIndex = i % 8;

                result[i] = ((response[start + byteIndex] >> bitIndex) & 1) == 1;
            }

            return result;
        }

        /// <summary>
        /// 尝试解析线圈值：数据不足或长度非法时返回 false（不抛异常），供结果式高层 API 使用。
        /// </summary>
        internal static bool TryParseCoils(byte[] response, int length, out bool[] values)
        {
            values = Array.Empty<bool>();
            if (response == null || length <= 0)
                return false;

            int expectedByteCount = (length + 7) / 8;
            if (response.Length < 3 + expectedByteCount)
                return false;

            values = new bool[length];
            for (int i = 0; i < length; i++)
                values[i] = ((response[3 + i / 8] >> (i % 8)) & 1) == 1;

            return true;
        }

        /// <summary>
        /// 尝试解析寄存器值：数据不足或长度非法时返回 false（不抛异常），供结果式高层 API 使用。
        /// </summary>
        internal static bool TryParseRegisters(byte[] response, int length, out ushort[] values)
        {
            values = Array.Empty<ushort>();
            if (response == null || length <= 0)
                return false;

            if (response.Length < 3 + length * 2)
                return false;

            values = new ushort[length];
            for (int i = 0; i < length * 2; i += 2)
                values[i / 2] = (ushort)((response[3 + i] << 8) | response[3 + i + 1]);

            return true;
        }

        /// <summary>
        /// Parses register values from a Modbus response frame.
        /// </summary>
        public static ushort[] ParseRegisters(byte[] response, int length)
        {
            if (response == null)
                throw new ArgumentNullException(nameof(response), "The response data cannot be null.");

            if (length <= 0)
                throw new ArgumentException("Length must be greater than 0.", nameof(length));

            if (response.Length < 3 + length * 2)
                throw new ArgumentException("The response data is not enough for the requested length.", nameof(response));

            ushort[] result = new ushort[length];

            for (int i = 0; i < length * 2; i += 2)
            {
                var index = 3 + i;
                result[i / 2] = (ushort)((response[index] << 8) | response[index + 1]);
            }
            return result;
        }

        public static bool VerifyAddress(string address) => !string.IsNullOrEmpty(address);

        public static bool VerifyPort(int port) => port is >= 1 and <= 65535;

        private static bool IsValidReadWriteMultipleRegistersRequest(byte[]? data)
        {
            // Data layout:
            // ReadStart(2), ReadQty(2), WriteStart(2), WriteQty(2), WriteByteCount(1), WriteData...
            if (data is null || data.Length < 9)
                return false;

            ushort readQuantity = (ushort)((data[2] << 8) | data[3]);
            ushort writeQuantity = (ushort)((data[6] << 8) | data[7]);
            byte writeByteCount = data[8];

            return readQuantity is >= 1 and <= 125
                && writeQuantity is >= 1 and <= 121
                && writeByteCount == writeQuantity * 2
                && data.Length == 9 + writeByteCount;
        }
    }
}
