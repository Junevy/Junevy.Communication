using Junevy.Communication.Modbus.Core.Interfaces;
using Junevy.Communication.Modbus.Core.Models;

namespace Junevy.Communication.Modbus.Extensions
{
    /// <summary>
    /// 诊断与状态扩展方法（功能码 0x07 / 0x08 / 0x0B / 0x0C / 0x11）。
    /// 命名空间 Junevy.Communication.Modbus.Extensions，必须用扩展方法语法调用。
    /// </summary>
    public static class ModbusDiagnosticsExtensions
    {
        public static ModbusResult<byte> ReadExceptionStatus(this IModbus modBus, byte slaveId)
        {
            var request = ModbusRequestExecutor.CreateRequest(slaveId, ModbusFunctionCode.ReadExceptionStatus);
            return ModbusPayloadCodec.MapExceptionStatus(ModbusRequestExecutor.Execute(modBus, request));
        }

        public static async ValueTask<ModbusResult<byte>> ReadExceptionStatusAsync(
            this IModbus modBus,
            byte slaveId,
            CancellationToken cancellationToken = default)
        {
            var request = ModbusRequestExecutor.CreateRequest(slaveId, ModbusFunctionCode.ReadExceptionStatus);
            var raw = await ModbusRequestExecutor.ExecuteAsync(modBus, request, cancellationToken);
            return ModbusPayloadCodec.MapExceptionStatus(raw);
        }

        public static ModbusResult<byte[]> Diagnostics(this IModbus modBus, byte slaveId, ushort subFunction, ushort data)
            => Diagnostics(modBus, slaveId, subFunction, data.ToBigEndian());

        public static ModbusResult<byte[]> Diagnostics(this IModbus modBus, byte slaveId, ushort subFunction, byte[] data)
        {
            byte[] payload = ModbusPayloadCodec.BuildDiagnosticsData(subFunction, data);
            var request = ModbusRequestExecutor.CreateRequest(slaveId, ModbusFunctionCode.Diagnostics, 0, 0, payload);
            return ModbusRequestExecutor.Execute(modBus, request);
        }

        public static ValueTask<ModbusResult<byte[]>> DiagnosticsAsync(
            this IModbus modBus,
            byte slaveId,
            ushort subFunction,
            ushort data,
            CancellationToken cancellationToken = default)
            => DiagnosticsAsync(modBus, slaveId, subFunction, data.ToBigEndian(), cancellationToken);

        public static ValueTask<ModbusResult<byte[]>> DiagnosticsAsync(
            this IModbus modBus,
            byte slaveId,
            ushort subFunction,
            byte[] data,
            CancellationToken cancellationToken = default)
        {
            byte[] payload = ModbusPayloadCodec.BuildDiagnosticsData(subFunction, data);
            var request = ModbusRequestExecutor.CreateRequest(slaveId, ModbusFunctionCode.Diagnostics, 0, 0, payload);
            return ModbusRequestExecutor.ExecuteAsync(modBus, request, cancellationToken);
        }

        public static ModbusResult<ModbusCommEventCounter> GetCommEventCounter(this IModbus modBus, byte slaveId)
        {
            var request = ModbusRequestExecutor.CreateRequest(slaveId, ModbusFunctionCode.GetCommEventCounter);
            return ModbusPayloadCodec.MapCommEventCounter(ModbusRequestExecutor.Execute(modBus, request));
        }

        public static async ValueTask<ModbusResult<ModbusCommEventCounter>> GetCommEventCounterAsync(
            this IModbus modBus,
            byte slaveId,
            CancellationToken cancellationToken = default)
        {
            var request = ModbusRequestExecutor.CreateRequest(slaveId, ModbusFunctionCode.GetCommEventCounter);
            var raw = await ModbusRequestExecutor.ExecuteAsync(modBus, request, cancellationToken);
            return ModbusPayloadCodec.MapCommEventCounter(raw);
        }

        public static ModbusResult<ModbusCommEventLog> GetCommEventLog(this IModbus modBus, byte slaveId)
        {
            var request = ModbusRequestExecutor.CreateRequest(slaveId, ModbusFunctionCode.GetCommEventLog);
            return ModbusPayloadCodec.MapCommEventLog(ModbusRequestExecutor.Execute(modBus, request));
        }

        public static async ValueTask<ModbusResult<ModbusCommEventLog>> GetCommEventLogAsync(
            this IModbus modBus,
            byte slaveId,
            CancellationToken cancellationToken = default)
        {
            var request = ModbusRequestExecutor.CreateRequest(slaveId, ModbusFunctionCode.GetCommEventLog);
            var raw = await ModbusRequestExecutor.ExecuteAsync(modBus, request, cancellationToken);
            return ModbusPayloadCodec.MapCommEventLog(raw);
        }

        public static ModbusResult<byte[]> ReportServerId(this IModbus modBus, byte slaveId)
        {
            var request = ModbusRequestExecutor.CreateRequest(slaveId, ModbusFunctionCode.ReportServerId);
            return ModbusPayloadCodec.MapByteCountPayload(
                ModbusRequestExecutor.Execute(modBus, request), "Report server id failed.");
        }

        public static async ValueTask<ModbusResult<byte[]>> ReportServerIdAsync(
            this IModbus modBus,
            byte slaveId,
            CancellationToken cancellationToken = default)
        {
            var request = ModbusRequestExecutor.CreateRequest(slaveId, ModbusFunctionCode.ReportServerId);
            var raw = await ModbusRequestExecutor.ExecuteAsync(modBus, request, cancellationToken);
            return ModbusPayloadCodec.MapByteCountPayload(raw, "Report server id failed.");
        }
    }
}