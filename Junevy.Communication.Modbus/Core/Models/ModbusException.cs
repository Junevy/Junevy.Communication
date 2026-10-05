namespace Junevy.Communication.Modbus.Core.Models
{
    /// <summary>
    /// Represents a Modbus wire-level exception response from a server device
    /// (function code with the MSB set, carrying a <see cref="ModbusExceptionCode"/>).
    /// Parameter validation failures use standard .NET exceptions instead.
    /// </summary>
    public class ModbusException : Exception
    {
        public ModbusExceptionCode ErrorCode { get; }

        public ModbusException(ModbusExceptionCode errorCode, string message) : base(message)
        {
            ErrorCode = errorCode;
        }

        public ModbusException(ModbusExceptionCode errorCode, string message, Exception innerException) : base(message, innerException)
        {
            ErrorCode = errorCode;
        }
    }
}
