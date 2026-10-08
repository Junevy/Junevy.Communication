using Junevy.Communication.Modbus.Core.Interfaces;
using Junevy.Communication.Modbus.Core.Models;

namespace Junevy.Communication.Modbus.Extensions
{
    /// <summary>
    /// 扩展方法的执行核心：构造请求、发送、规范化结果（剥离 MBAP 头、识别从站异常响应）。
    /// 仅供本命名空间内的三个公开扩展类使用。
    /// </summary>
    internal static class ModbusRequestExecutor
    {
        internal static ModbusResult<byte[]> Execute(
            IModbus modBus,
            ModbusRequest request)
        {
            var result = modBus.Request(request);
            return NormalizeRawResult(modBus.ProtocolType, result);
        }

        internal static async ValueTask<ModbusResult<byte[]>> ExecuteAsync(
            IModbus modBus,
            ModbusRequest request,
            CancellationToken cancellationToken)
        {
            var result = await modBus.RequestAsync(request, cancellationToken);
            return NormalizeRawResult(modBus.ProtocolType, result);
        }

        /// <summary>
        /// 把已发送的请求映射为最终结果：发送侧为 <see cref="ModbusResult{T}"/>，映射函数可再加工。
        /// </summary>
        internal static async ValueTask<TResult> ExecuteAndMapAsync<TResult>(
            IModbus modBus,
            ModbusRequest request,
            CancellationToken cancellationToken,
            Func<ModbusResult<byte[]>, TResult> map)
        {
            var raw = await ExecuteAsync(modBus, request, cancellationToken);
            return map(raw);
        }

        internal static ModbusResult<byte[]> NormalizeRawResult(
            ModbusProtocolType protocolType,
            ModbusResult<byte[]> result)
        {
            if (!result.IsSuccess || result.Data == null || result.Data.Length == 0)
                return ModbusResult<byte[]>.Fail(result.ErrorMessage ?? "Request failed.", result.ErrorKind, result.Data);

            byte[] pdu = ExtractPdu(result.Data, protocolType);
            if (pdu.Length >= 2 && (pdu[1] & 0x80) != 0)
            {
                string message = pdu.Length >= 3
                    ? $"Modbus exception response. Function=0x{pdu[1]:X2}, Code=0x{pdu[2]:X2}."
                    : $"Modbus exception response. Function=0x{pdu[1]:X2}.";
                return ModbusResult<byte[]>.Fail(message, ModbusErrorKind.ModbusException, pdu);
            }

            return ModbusResult<byte[]>.Success(pdu);
        }

        /// <summary>TCP 剥离 6 字节 MBAP 头；RTU 原样返回（含从站号与 CRC 之前的内容）。</summary>
        internal static byte[] ExtractPdu(byte[] frame, ModbusProtocolType protocolType)
        {
            int offset = protocolType == ModbusProtocolType.TCP ? 6 : 0;
            if (frame.Length <= offset)
                return Array.Empty<byte>();

            byte[] pdu = new byte[frame.Length - offset];
            Buffer.BlockCopy(frame, offset, pdu, 0, pdu.Length);
            return pdu;
        }

        internal static ModbusRequest CreateRequest(
            byte slaveId,
            ModbusFunctionCode functionCode,
            ushort start = 0,
            ushort length = 0,
            byte[]? data = null)
            => new()
            {
                SlaveId = slaveId,
                FunctionCode = functionCode,
                StartAddress = start,
                Quantity = length,
                Data = data
            };
    }
}