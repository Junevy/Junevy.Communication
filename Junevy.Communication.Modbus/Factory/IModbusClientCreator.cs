using Junevy.Communication.Modbus.Core.Interfaces;

namespace Junevy.Communication.Modbus.Factory
{
    /// <summary>
    /// 传输创建策略：按配置类型创建 <see cref="IModbus"/> 客户端。
    /// 新增一种传输（例如 RTU over TCP）只需实现本接口并注册，无需修改 <see cref="ModbusFactory"/>。
    /// </summary>
    public interface IModbusClientCreator
    {
        /// <summary>本创建器处理的配置类型（精确类型）。</summary>
        Type ConfigType { get; }

        /// <summary>校验并补全默认值。会修改传入的配置对象。配置非法时抛出 ArgumentException。</summary>
        void Normalize(IModbusConfig config);

        /// <summary>创建客户端。config 已通过 Normalize。</summary>
        IModbus Create(IModbusConfig config);

        /// <summary>用于日志的目标描述，例如 "192.168.1.100:502" 或 "COM3"。</summary>
        string Describe(IModbusConfig config);
    }
}