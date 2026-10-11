namespace Junevy.Communication.Channels;

/// <summary>
/// 通道的物理介质（P25-D4）。通道创建器通过 <see cref="IChannelCreator.Medium"/> 声明，协议据此选择帧格式（例如 MELSEC 的以太网帧 3E / 4E 与串口帧 4C）。
/// </summary>
public enum ChannelMedium
{
    /// <summary>以太网（TCP、UDP）。</summary>
    Network,

    /// <summary>串口。</summary>
    Serial,
}
