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
                    request.Length is >= 1 and <= 2000,

                ModbusFunctionCode.ReadHoldingRegisters
                    or ModbusFunctionCode.ReadInputRegisters =>
                    request.Length is >= 1 and <= 125,

                ModbusFunctionCode.WriteCoil
                    or ModbusFunctionCode.WriteHoldingRegister =>
                    request.Data is { Length: 2 },

                ModbusFunctionCode.ReadExceptionStatus
                    or ModbusFunctionCode.GetCommEventCounter
                    or ModbusFunctionCode.GetCommEventLog
                    or ModbusFunctionCode.ReportServerId =>
                    true,

                ModbusFunctionCode.Diagnostics =>
                    request.Data is not null && request.Data.Length >= 4 && request.Data.Length <= 252,

                ModbusFunctionCode.WriteMultipleCoils =>
                    request.Length is >= 1 and <= 1968
                    && request.Data is not null
                    && request.Data.Length == (request.Length + 7) / 8,

                ModbusFunctionCode.WriteMultipleHoldingRegisters =>
                    request.Length is >= 1 and <= 123
                    && request.Data is not null
                    && request.Data.Length == request.Length * 2,

                ModbusFunctionCode.MaskWriteRegister =>
                    request.Data is { Length: 4 },

                ModbusFunctionCode.ReadWriteMultipleRegisters =>
                    IsValidReadWriteMultipleRegistersRequest(request.Data),

                _ => false
            };
        }

        /// <summary>
        /// Builds a Modbus request frame from a request object.
        /// </summary>
        /// <exception cref="ModbusException">Thrown when the request is invalid.</exception>
        public static byte[] BuildRequestFrame(ModbusRequest request)
            => FrameBuilder.BuildRequestFrame(request);

        /// <summary>
        /// Parses coil values from a Modbus response frame.
        /// </summary>
        public static bool[] ParseCoils(byte[] response, int length)
        {
            if (response == null)
                throw new ModbusException(ModbusErrorCode.InvalidData, "The response data cannot be null.");

            if (length <= 0)
                throw new ModbusException(ModbusErrorCode.InvalidValue, "Length must be greater than 0.");

            int expectedByteCount = (length + 7) / 8;
            if (response.Length < 2 + expectedByteCount)
                throw new ModbusException(ModbusErrorCode.InvalidData,
                    "The response data is not enough for the requested length.");

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
        /// Parses register values from a Modbus response frame.
        /// </summary>
        public static ushort[] ParseRegisters(byte[] response, int length)
        {
            if (response == null)
                throw new ModbusException(ModbusErrorCode.InvalidData, "The response data cannot be null.");

            if (length <= 0)
                throw new ModbusException(ModbusErrorCode.InvalidData, "Length must be greater than 0.");

            if (response.Length < 3 + length * 2)
                throw new ModbusException(ModbusErrorCode.InvalidData,
                    "The response data is not enough for the requested length.");

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
