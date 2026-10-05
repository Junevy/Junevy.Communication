using Junevy.Communication.Modbus.Core.Models;

namespace Junevy.Communication.Modbus.Core.Interfaces
{
    /// <summary>
    /// Builds Modbus request frames for protocol transports.
    /// The protocol is always supplied as an explicit argument; implementations
    /// never read or mutate <see cref="ModbusRequest.ProtocolType"/>.
    /// </summary>
    public interface IModbusFrameBuilder
    {
        /// <summary>
        /// Gets the exact request ADU length for the given protocol.
        /// </summary>
        int GetRequestFrameLength(ModbusRequest request, ModbusProtocolType protocolType);

        /// <summary>
        /// Writes the request ADU into the supplied buffer.
        /// </summary>
        bool TryWriteRequestFrame(ModbusRequest request, ModbusProtocolType protocolType, Span<byte> destination, out int bytesWritten);

        /// <summary>
        /// Builds a request ADU as a new array for compatibility APIs.
        /// </summary>
        byte[] BuildRequestFrame(ModbusRequest request, ModbusProtocolType protocolType);
    }
}
