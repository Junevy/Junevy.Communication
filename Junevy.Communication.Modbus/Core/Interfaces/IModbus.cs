using Junevy.Communication.Modbus.Core.Models;

namespace Junevy.Communication.Modbus.Core.Interfaces
{
    /// <summary>
    /// Modbus 接口，用于定义 Modbus 操作。
    /// </summary>
    public interface IModbus : IDisposable
    {
        /// <summary>
        /// 当前对象的协议类型
        /// </summary>
        public ModbusProtocolType ProtocolType { get; }

        /// <summary>
        /// 是否已连接到 Modbus 从站
        /// </summary>
        public bool IsConnected { get; }

        /// <summary>
        /// 连接 Modbus 从站。
        /// </summary>
        /// <returns>是否成功连接。</returns>
        public bool Connect();

        /// <summary>
        /// 异步连接 Modbus 从站。取消令牌在等待请求锁与建立连接两处生效。
        /// </summary>
        /// <param name="cancellationToken">取消令牌；用户取消时抛出 <see cref="OperationCanceledException"/>。</param>
        /// <returns>连接成功返回 true；连接失败或连接超时返回 false。</returns>
        public Task<bool> ConnectAsync(CancellationToken cancellationToken = default);

        /// <summary>
        /// 断开 Modbus 从站连接。
        /// </summary>
        public void Disconnect();

        /// <summary>
        /// 发送 Modbus 指令
        /// </summary>
        /// <param name="tx">Modbus 指令</param>
        /// <returns>Modbus 指令的响应</returns>
        public ModbusResult<byte[]> Request(ModbusRequest tx);

        /// <summary>
        /// 异步发送 Modbus 指令
        /// </summary>
        /// <param name="tx">Modbus 指令</param>
        /// <param name="cancellationToken">取消令牌</param>
        /// <returns>Modbus 指令的响应</returns>
        public Task<ModbusResult<byte[]>> RequestAsync(ModbusRequest tx, CancellationToken cancellationToken = default);
    }
}
