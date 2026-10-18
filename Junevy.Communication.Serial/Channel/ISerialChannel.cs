using Junevy.Communication.Channels;

namespace Junevy.Communication.Serial;

/// <summary>
/// 串口通道的公开接口（设计文档第 8 节）。同一条串口可以由多个逻辑设备经别名共享（RS-485 多站），请求由通道串行化。
/// </summary>
public interface ISerialChannel : IClientChannel
{
    /// <summary>构造时传入的配置对象。运行行为只使用构造时的快照（D5）。</summary>
    SerialChannelConfig Config { get; }
}
