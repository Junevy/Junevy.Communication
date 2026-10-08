using Junevy.Communication.Modbus.Core.Interfaces;
using Junevy.Communication.Modbus.Core.Models;

namespace Junevy.Communication.Modbus.Extensions
{
    /// <summary>
    /// 寄存器扩展方法（功能码 0x03 / 0x04 / 0x06 / 0x10 / 0x16 / 0x17）。
    /// 命名空间 Junevy.Communication.Modbus.Extensions，必须用扩展方法语法调用。
    /// </summary>
    public static class ModbusRegisterExtensions
    {
        public static ModbusResult<ushort[]> ReadHoldingRegisters(this IModbus modBus, byte slaveId, ushort start, ushort length)
        {
            ModbusPayloadCodec.ValidateRegisterQuantity(length, 125, nameof(ReadHoldingRegisters));
            var request = ModbusRequestExecutor.CreateRequest(slaveId, ModbusFunctionCode.ReadHoldingRegisters, start, length);
            return ModbusPayloadCodec.MapRead(
                ModbusRequestExecutor.Execute(modBus, request), length, ModbusPayloadCodec.TryParseRegisters);
        }

        public static ValueTask<ModbusResult<ushort[]>> ReadHoldingRegistersAsync(
            this IModbus modBus,
            byte slaveId,
            ushort start,
            ushort length,
            CancellationToken cancellationToken = default)
        {
            ModbusPayloadCodec.ValidateRegisterQuantity(length, 125, nameof(ReadHoldingRegistersAsync));
            var request = ModbusRequestExecutor.CreateRequest(slaveId, ModbusFunctionCode.ReadHoldingRegisters, start, length);
            return ModbusRequestExecutor.ExecuteAndMapAsync(
                modBus, request, cancellationToken,
                raw => ModbusPayloadCodec.MapRead(raw, length, ModbusPayloadCodec.TryParseRegisters));
        }

        public static ModbusResult<ushort[]> ReadInputRegisters(this IModbus modBus, byte slaveId, ushort start, ushort length)
        {
            ModbusPayloadCodec.ValidateRegisterQuantity(length, 125, nameof(ReadInputRegisters));
            var request = ModbusRequestExecutor.CreateRequest(slaveId, ModbusFunctionCode.ReadInputRegisters, start, length);
            return ModbusPayloadCodec.MapRead(
                ModbusRequestExecutor.Execute(modBus, request), length, ModbusPayloadCodec.TryParseRegisters);
        }

        public static ValueTask<ModbusResult<ushort[]>> ReadInputRegistersAsync(
            this IModbus modBus,
            byte slaveId,
            ushort start,
            ushort length,
            CancellationToken cancellationToken = default)
        {
            ModbusPayloadCodec.ValidateRegisterQuantity(length, 125, nameof(ReadInputRegistersAsync));
            var request = ModbusRequestExecutor.CreateRequest(slaveId, ModbusFunctionCode.ReadInputRegisters, start, length);
            return ModbusRequestExecutor.ExecuteAndMapAsync(
                modBus, request, cancellationToken,
                raw => ModbusPayloadCodec.MapRead(raw, length, ModbusPayloadCodec.TryParseRegisters));
        }

        public static ModbusResult<byte[]> WriteSingleRegister(this IModbus modBus, byte slaveId, ushort start, ushort value)
        {
            var request = ModbusRequestExecutor.CreateRequest(slaveId, ModbusFunctionCode.WriteHoldingRegister, start, 1, value.ToBigEndian());
            return ModbusRequestExecutor.Execute(modBus, request);
        }

        public static ValueTask<ModbusResult<byte[]>> WriteSingleRegisterAsync(
            this IModbus modBus,
            byte slaveId,
            ushort start,
            ushort value,
            CancellationToken cancellationToken = default)
        {
            var request = ModbusRequestExecutor.CreateRequest(slaveId, ModbusFunctionCode.WriteHoldingRegister, start, 1, value.ToBigEndian());
            return ModbusRequestExecutor.ExecuteAsync(modBus, request, cancellationToken);
        }

        public static ModbusResult<byte[]> WriteMultipleRegisters(this IModbus modBus, byte slaveId, ushort start, ushort[] values)
        {
            ModbusPayloadCodec.ValidateWriteRegisters(values, 123, nameof(WriteMultipleRegisters));
            var request = ModbusRequestExecutor.CreateRequest(slaveId, ModbusFunctionCode.WriteMultipleHoldingRegisters, start, (ushort)values.Length, values.ToBigEndianByteArray());
            return ModbusRequestExecutor.Execute(modBus, request);
        }

        public static ValueTask<ModbusResult<byte[]>> WriteMultipleRegistersAsync(
            this IModbus modBus,
            byte slaveId,
            ushort start,
            ushort[] values,
            CancellationToken cancellationToken = default)
        {
            ModbusPayloadCodec.ValidateWriteRegisters(values, 123, nameof(WriteMultipleRegistersAsync));
            var request = ModbusRequestExecutor.CreateRequest(slaveId, ModbusFunctionCode.WriteMultipleHoldingRegisters, start, (ushort)values.Length, values.ToBigEndianByteArray());
            return ModbusRequestExecutor.ExecuteAsync(modBus, request, cancellationToken);
        }

        public static ModbusResult<byte[]> MaskWriteRegister(this IModbus modBus, byte slaveId, ushort start, ushort andMask, ushort orMask)
        {
            byte[] data = ModbusPayloadCodec.Combine(andMask.ToBigEndian(), orMask.ToBigEndian());
            var request = ModbusRequestExecutor.CreateRequest(slaveId, ModbusFunctionCode.MaskWriteRegister, start, 1, data);
            return ModbusRequestExecutor.Execute(modBus, request);
        }

        public static ValueTask<ModbusResult<byte[]>> MaskWriteRegisterAsync(
            this IModbus modBus,
            byte slaveId,
            ushort start,
            ushort andMask,
            ushort orMask,
            CancellationToken cancellationToken = default)
        {
            byte[] data = ModbusPayloadCodec.Combine(andMask.ToBigEndian(), orMask.ToBigEndian());
            var request = ModbusRequestExecutor.CreateRequest(slaveId, ModbusFunctionCode.MaskWriteRegister, start, 1, data);
            return ModbusRequestExecutor.ExecuteAsync(modBus, request, cancellationToken);
        }

        public static ModbusResult<ushort[]> ReadWriteMultipleRegisters(
            this IModbus modBus,
            byte slaveId,
            ushort readStart,
            ushort readLength,
            ushort writeStart,
            ushort[] writeValues)
        {
            ModbusPayloadCodec.ValidateRegisterQuantity(readLength, 125, nameof(ReadWriteMultipleRegisters));
            ModbusPayloadCodec.ValidateWriteRegisters(writeValues, 121, nameof(ReadWriteMultipleRegisters));

            byte[] data = ModbusPayloadCodec.BuildReadWriteMultipleRegistersData(readStart, readLength, writeStart, writeValues);
            var request = ModbusRequestExecutor.CreateRequest(slaveId, ModbusFunctionCode.ReadWriteMultipleRegisters, readStart, readLength, data);
            return ModbusPayloadCodec.MapRead(
                ModbusRequestExecutor.Execute(modBus, request), readLength, ModbusPayloadCodec.TryParseRegisters);
        }

        public static ValueTask<ModbusResult<ushort[]>> ReadWriteMultipleRegistersAsync(
            this IModbus modBus,
            byte slaveId,
            ushort readStart,
            ushort readLength,
            ushort writeStart,
            ushort[] writeValues,
            CancellationToken cancellationToken = default)
        {
            ModbusPayloadCodec.ValidateRegisterQuantity(readLength, 125, nameof(ReadWriteMultipleRegistersAsync));
            ModbusPayloadCodec.ValidateWriteRegisters(writeValues, 121, nameof(ReadWriteMultipleRegistersAsync));

            byte[] data = ModbusPayloadCodec.BuildReadWriteMultipleRegistersData(readStart, readLength, writeStart, writeValues);
            var request = ModbusRequestExecutor.CreateRequest(slaveId, ModbusFunctionCode.ReadWriteMultipleRegisters, readStart, readLength, data);
            return ModbusRequestExecutor.ExecuteAndMapAsync(
                modBus, request, cancellationToken,
                raw => ModbusPayloadCodec.MapRead(raw, readLength, ModbusPayloadCodec.TryParseRegisters));
        }
    }
}