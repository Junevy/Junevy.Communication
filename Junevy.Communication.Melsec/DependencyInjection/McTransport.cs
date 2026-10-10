namespace Junevy.Communication.Melsec.DependencyInjection;

/// <summary>MELSEC 客户端的传输。决定 <see cref="McClientConfig"/> 中必须设置哪一个传输配置。</summary>
public enum McTransport
{
    /// <summary>TCP，3E 或 4E；需要 <see cref="McClientConfig.Tcp"/>。</summary>
    Tcp,

    /// <summary>UDP，3E 或 4E；需要 <see cref="McClientConfig.Udp"/>。</summary>
    Udp,

    /// <summary>
    /// 串口，4C（格式 4 或格式 5）；需要 <see cref="McClientConfig.Serial"/>，且 <see cref="McClientConfig.Options"/> 的帧类型必须为
    /// <see cref="McFrameType.Frame4C"/>。
    /// </summary>
    Serial,
}
