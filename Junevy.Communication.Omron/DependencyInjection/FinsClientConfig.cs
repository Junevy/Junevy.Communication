using Junevy.Communication.Plc;
using Junevy.Communication.Tcp;

namespace Junevy.Communication.Omron.DependencyInjection;

/// <summary>
/// FINS 客户端的依赖注入配置。FINS 只走 FINSoverTCP（P4-D2），因此传输固定为 <see cref="TcpClientChannelConfig"/>，
/// 不提供传输开关；置空 <see cref="Tcp"/> 时创建器抛出 <see cref="ArgumentException"/>。
/// 形状与 appsettings 一致，可直接绑定，例如 <c>configuration.GetSection("Omron").Get&lt;FinsClientConfig&gt;()</c>；
/// <see cref="PlcDataFormat"/> 只能由构造函数创建，因此不在 JSON 里配置。
/// </summary>
public sealed class FinsClientConfig : IPlcClientConfig
{
    /// <summary>TCP 传输配置；分帧与应答关联由协议设定，保持默认值即可。</summary>
    public TcpClientChannelConfig Tcp { get; set; } = new TcpClientChannelConfig();

    /// <summary>FINS 选项：客户端节点号、目的网络号与单元号、数据格式；默认由握手自动分配节点号、CDAB 字序。</summary>
    public FinsClientOptions Options { get; set; } = new FinsClientOptions();
}
