using Junevy.Communication.Modbus.Core.Interfaces;
using Junevy.Communication.Modbus.Core.Models;
using Junevy.Communication.Modbus.Utils;
using System.Buffers.Binary;

namespace Junevy.Communication.Modbus.Core.Framing
{
    /// <summary>
    /// Protocol-aware Modbus request frame writer.
    /// </summary>
    public sealed class ModbusFrameBuilder : IModbusFrameBuilder
    {
        public const int MaxRtuAduLength = 256;
        public const int MaxTcpAduLength = 260;

        public int GetRequestFrameLength(ModbusRequest request, ModbusProtocolType protocolType)
        {
            if (request == null)
                throw new ArgumentNullException(nameof(request));
            if (!ModbusHelper.CheckRequest(request))
                throw new ArgumentException("Invalid request.", nameof(request));

            int rtuLength = GetRtuFrameLength(request);
            return protocolType switch
            {
                ModbusProtocolType.RTU => rtuLength,
                ModbusProtocolType.TCP => rtuLength + 4,
                _ => throw new ArgumentException("The protocol is not supported.", nameof(protocolType))
            };
        }

        public bool TryWriteRequestFrame(ModbusRequest request, ModbusProtocolType protocolType, Span<byte> destination, out int bytesWritten)
        {
            bytesWritten = 0;
            if (request == null)
                throw new ArgumentNullException(nameof(request));

            if (!ModbusHelper.CheckRequest(request))
                return false;

            return protocolType switch
            {
                ModbusProtocolType.RTU => TryWriteRtuRequestFrame(request, destination, out bytesWritten),
                ModbusProtocolType.TCP => TryWriteTcpRequestFrame(request, destination, out bytesWritten),
                _ => false
            };
        }

        public byte[] BuildRequestFrame(ModbusRequest request, ModbusProtocolType protocolType)
        {
            int length = GetRequestFrameLength(request, protocolType);
            var frame = new byte[length];
            if (!TryWriteRequestFrame(request, protocolType, frame, out int written) || written != length)
                throw new InvalidOperationException("Failed to build request frame.");

            return frame;
        }

        private static int GetRtuFrameLength(ModbusRequest request)
        {
            return request.FunctionCode switch
            {
                ModbusFunctionCode.ReadCoils
                    or ModbusFunctionCode.ReadDiscreteInputs
                    or ModbusFunctionCode.ReadHoldingRegisters
                    or ModbusFunctionCode.ReadInputRegisters => 8,

                ModbusFunctionCode.WriteCoil
                    or ModbusFunctionCode.WriteHoldingRegister => 8,

                ModbusFunctionCode.ReadExceptionStatus
                    or ModbusFunctionCode.GetCommEventCounter
                    or ModbusFunctionCode.GetCommEventLog
                    or ModbusFunctionCode.ReportServerId => 4,

                ModbusFunctionCode.Diagnostics => 4 + request.Data!.Length,

                ModbusFunctionCode.WriteMultipleCoils
                    or ModbusFunctionCode.WriteMultipleHoldingRegisters => 9 + request.Data!.Length,

                ModbusFunctionCode.MaskWriteRegister => 10,

                ModbusFunctionCode.ReadWriteMultipleRegisters => 4 + request.Data!.Length,

                _ => throw new ArgumentException("The function code is not supported.", nameof(request))
            };
        }

        private static bool TryWriteTcpRequestFrame(ModbusRequest request, Span<byte> destination, out int bytesWritten)
        {
            bytesWritten = 0;
            int rtuLength = GetRtuFrameLength(request);
            int tcpLength = rtuLength + 4;
            if (destination.Length < tcpLength)
                return false;

            ushort transactionId = request.TransactionId;
            BinaryPrimitives.WriteUInt16BigEndian(destination.Slice(0, 2), transactionId);
            destination[2] = 0x00;
            destination[3] = 0x00;
            BinaryPrimitives.WriteUInt16BigEndian(destination.Slice(4, 2), (ushort)(rtuLength - 2));

            if (!TryWriteRtuPdu(request, destination.Slice(6), out int pduLength))
                return false;

            bytesWritten = 6 + pduLength;
            return bytesWritten == tcpLength;
        }

        private static bool TryWriteRtuRequestFrame(ModbusRequest request, Span<byte> destination, out int bytesWritten)
        {
            bytesWritten = 0;
            int frameLength = GetRtuFrameLength(request);
            if (destination.Length < frameLength)
                return false;

            if (!TryWriteRtuPdu(request, destination, out int pduLength))
                return false;

            ushort crc = Crc16Helper.ComputeCrc(destination.Slice(0, pduLength));
            BinaryPrimitives.WriteUInt16LittleEndian(destination.Slice(pduLength, 2), crc);
            bytesWritten = pduLength + 2;
            return bytesWritten == frameLength;
        }

        private static bool TryWriteRtuPdu(ModbusRequest request, Span<byte> destination, out int bytesWritten)
        {
            bytesWritten = 0;
            int pduLength = GetRtuFrameLength(request) - 2;
            if (destination.Length < pduLength)
                return false;

            destination[0] = request.SlaveId;
            destination[1] = (byte)request.FunctionCode;

            switch (request.FunctionCode)
            {
                case ModbusFunctionCode.ReadCoils:
                case ModbusFunctionCode.ReadDiscreteInputs:
                case ModbusFunctionCode.ReadHoldingRegisters:
                case ModbusFunctionCode.ReadInputRegisters:
                    BinaryPrimitives.WriteUInt16BigEndian(destination.Slice(2, 2), request.StartAddress);
                    BinaryPrimitives.WriteUInt16BigEndian(destination.Slice(4, 2), request.Quantity);
                    bytesWritten = 6;
                    return true;

                case ModbusFunctionCode.WriteCoil:
                case ModbusFunctionCode.WriteHoldingRegister:
                    BinaryPrimitives.WriteUInt16BigEndian(destination.Slice(2, 2), request.StartAddress);
                    request.Data!.AsSpan(0, 2).CopyTo(destination.Slice(4));
                    bytesWritten = 6;
                    return true;

                case ModbusFunctionCode.ReadExceptionStatus:
                case ModbusFunctionCode.GetCommEventCounter:
                case ModbusFunctionCode.GetCommEventLog:
                case ModbusFunctionCode.ReportServerId:
                    bytesWritten = 2;
                    return true;

                case ModbusFunctionCode.Diagnostics:
                    var diagnosticsData = request.Data!;
                    diagnosticsData.CopyTo(destination.Slice(2));
                    bytesWritten = 2 + diagnosticsData.Length;
                    return true;

                case ModbusFunctionCode.WriteMultipleCoils:
                case ModbusFunctionCode.WriteMultipleHoldingRegisters:
                    var multiWriteData = request.Data!;
                    BinaryPrimitives.WriteUInt16BigEndian(destination.Slice(2, 2), request.StartAddress);
                    BinaryPrimitives.WriteUInt16BigEndian(destination.Slice(4, 2), request.Quantity);
                    destination[6] = GetWriteByteCount(request);
                    multiWriteData.CopyTo(destination.Slice(7));
                    bytesWritten = 7 + multiWriteData.Length;
                    return true;

                case ModbusFunctionCode.MaskWriteRegister:
                    BinaryPrimitives.WriteUInt16BigEndian(destination.Slice(2, 2), request.StartAddress);
                    request.Data!.AsSpan(0, 4).CopyTo(destination.Slice(4));
                    bytesWritten = 8;
                    return true;

                case ModbusFunctionCode.ReadWriteMultipleRegisters:
                    var readWriteData = request.Data!;
                    readWriteData.CopyTo(destination.Slice(2));
                    bytesWritten = 2 + readWriteData.Length;
                    return true;

                default:
                    return false;
            }
        }

        private static byte GetWriteByteCount(ModbusRequest request)
        {
            return request.FunctionCode == ModbusFunctionCode.WriteMultipleCoils
                ? (byte)((request.Quantity + 7) / 8)
                : (byte)(request.Quantity * 2);
        }
    }
}
