namespace Junevy.Communication.Channels;

/// <summary>
/// 通道向上层交付数据的方式（P25-D4）。通道创建器通过 <see cref="IChannelCreator.Delivery"/> 声明，协议据此配置组件。
/// </summary>
public enum ChannelDelivery
{
    /// <summary>字节流：需要分帧（<see cref="ChannelComponents.FrameCodec"/> 或配置的 <see cref="FramingOptions"/>）。</summary>
    Stream,

    /// <summary>数据报：一个数据报就是一帧，不接受 <see cref="ChannelComponents.FrameCodec"/>。</summary>
    Datagram,
}
