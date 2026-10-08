using Junevy.Communication.Modbus.Core.Interfaces;
using Junevy.Communication.Modbus.Core.Models;

namespace Junevy.Communication.Modbus.Extensions
{
    /// <summary>
    /// 位操作扩展方法（功能码 0x01 / 0x02 / 0x05 / 0x0F）。
    /// 命名空间 Junevy.Communication.Modbus.Extensions，必须用扩展方法语法调用。
    /// </summary>
    public static class ModbusBitExtensions
    {
        public static ModbusResult<bool[]> ReadCoils(this IModbus modBus, byte slaveId, ushort start, ushort length)
        {
            ModbusPayloadCodec.ValidateBitQuantity(length, 2000, nameof(ReadCoils));
            var request = ModbusRequestExecutor.CreateRequest(slaveId, ModbusFunctionCode.ReadCoils, start, length);
            return ModbusPayloadCodec.MapRead(
                ModbusRequestExecutor.Execute(modBus, request), length, ModbusPayloadCodec.TryParseCoils);
        }

        public static ValueTask<ModbusResult<bool[]>> ReadCoilsAsync(
            this IModbus modBus,
            byte slaveId,
            ushort start,
            ushort length,
            CancellationToken cancellationToken = default)
        {
            ModbusPayloadCodec.ValidateBitQuantity(length, 2000, nameof(ReadCoilsAsync));
            var request = ModbusRequestExecutor.CreateRequest(slaveId, ModbusFunctionCode.ReadCoils, start, length);
            return ModbusRequestExecutor.ExecuteAndMapAsync(
                modBus, request, cancellationToken,
                raw => ModbusPayloadCodec.MapRead(raw, length, ModbusPayloadCodec.TryParseCoils));
        }

        public static ModbusResult<bool[]> ReadDiscreteInputs(this IModbus modBus, byte slaveId, ushort start, ushort length)
        {
            ModbusPayloadCodec.ValidateBitQuantity(length, 2000, nameof(ReadDiscreteInputs));
            var request = ModbusRequestExecutor.CreateRequest(slaveId, ModbusFunctionCode.ReadDiscreteInputs, start, length);
            return ModbusPayloadCodec.MapRead(
                ModbusRequestExecutor.Execute(modBus, request), length, ModbusPayloadCodec.TryParseCoils);
        }

        public static ValueTask<ModbusResult<bool[]>> ReadDiscreteInputsAsync(
            this IModbus modBus,
            byte slaveId,
            ushort start,
            ushort length,
            CancellationToken cancellationToken = default)
        {
            ModbusPayloadCodec.ValidateBitQuantity(length, 2000, nameof(ReadDiscreteInputsAsync));
            var request = ModbusRequestExecutor.CreateRequest(slaveId, ModbusFunctionCode.ReadDiscreteInputs, start, length);
            return ModbusRequestExecutor.ExecuteAndMapAsync(
                modBus, request, cancellationToken,
                raw => ModbusPayloadCodec.MapRead(raw, length, ModbusPayloadCodec.TryParseCoils));
        }

        public static ModbusResult<byte[]> WriteSingleCoil(this IModbus modBus, byte slaveId, ushort start, bool value)
        {
            byte[] data = new byte[] { (byte)(value ? 0xFF : 0x00), 0x00 };
            var request = ModbusRequestExecutor.CreateRequest(slaveId, ModbusFunctionCode.WriteCoil, start, 1, data);
            return ModbusRequestExecutor.Execute(modBus, request);
        }

        public static ValueTask<ModbusResult<byte[]>> WriteSingleCoilAsync(
            this IModbus modBus,
            byte slaveId,
            ushort start,
            bool value,
            CancellationToken cancellationToken = default)
        {
            byte[] data = new byte[] { (byte)(value ? 0xFF : 0x00), 0x00 };
            var request = ModbusRequestExecutor.CreateRequest(slaveId, ModbusFunctionCode.WriteCoil, start, 1, data);
            return ModbusRequestExecutor.ExecuteAsync(modBus, request, cancellationToken);
        }

        public static ModbusResult<byte[]> WriteMultipleCoils(this IModbus modBus, byte slaveId, ushort start, bool[] values)
        {
            byte[] data = ModbusPayloadCodec.PackCoils(values);
            var request = ModbusRequestExecutor.CreateRequest(slaveId, ModbusFunctionCode.WriteMultipleCoils, start, (ushort)values.Length, data);
            return ModbusRequestExecutor.Execute(modBus, request);
        }

        public static ValueTask<ModbusResult<byte[]>> WriteMultipleCoilsAsync(
            this IModbus modBus,
            byte slaveId,
            ushort start,
            bool[] values,
            CancellationToken cancellationToken = default)
        {
            byte[] data = ModbusPayloadCodec.PackCoils(values);
            var request = ModbusRequestExecutor.CreateRequest(slaveId, ModbusFunctionCode.WriteMultipleCoils, start, (ushort)values.Length, data);
            return ModbusRequestExecutor.ExecuteAsync(modBus, request, cancellationToken);
        }
    }
}