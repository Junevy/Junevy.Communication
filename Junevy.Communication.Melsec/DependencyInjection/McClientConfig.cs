using Junevy.Communication.Plc;
using Junevy.Communication.Serial;
using Junevy.Communication.Tcp;
using Junevy.Communication.Udp;

namespace Junevy.Communication.Melsec.DependencyInjection;

/// <summary>
/// MELSEC 客户端的依赖注入配置。<see cref="Transport"/> 选择传输，对应的传输配置必须设置，否则创建时抛出 <see cref="ArgumentException"/>。
/// 形状与 appsettings 一致，可直接绑定，例如 <c>configuration.GetSection("Mc").Get&lt;McClientConfig&gt;()</c>。
/// </summary>
public sealed class McClientConfig : IPlcClientConfig
{
    /// <summary>传输；默认 TCP。</summary>
    public McTransport Transport { get; set; } = McTransport.Tcp;

    /// <summary>TCP 传输配置；<see cref="Transport"/> 为 <see cref="McTransport.Tcp"/> 时必填。</summary>
    public TcpClientChannelConfig? Tcp { get; set; }

    /// <summary>UDP 传输配置；<see cref="Transport"/> 为 <see cref="McTransport.Udp"/> 时必填。</summary>
    public UdpChannelConfig? Udp { get; set; }

    /// <summary>串口传输配置；<see cref="Transport"/> 为 <see cref="McTransport.Serial"/> 时必填。</summary>
    public SerialChannelConfig? Serial { get; set; }

    /// <summary>MELSEC 选项：帧类型、编码、站号与监视定时器等；默认 3E 二进制（串口需设为 4C）。</summary>
    public McClientOptions Options { get; set; } = new McClientOptions();
}
