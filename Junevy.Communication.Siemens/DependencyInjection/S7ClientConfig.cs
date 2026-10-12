using Junevy.Communication.Plc;
using Junevy.Communication.Tcp;

namespace Junevy.Communication.Siemens.DependencyInjection;

/// <summary>
/// S7 客户端的依赖注入配置。S7 只走 ISO-on-TCP（P4-D2），因此传输固定为 <see cref="TcpClientChannelConfig"/>，
/// 不提供传输开关；置空 <see cref="Tcp"/> 时创建器抛出 <see cref="ArgumentException"/>。
/// 形状与 appsettings 一致，可直接绑定，例如 <c>configuration.GetSection("S7").Get&lt;S7ClientConfig&gt;()</c>；
/// <see cref="PlcDataFormat"/> 只能由构造函数创建，因此不在 JSON 里配置。
/// </summary>
public sealed class S7ClientConfig : IPlcClientConfig
{
    /// <summary>TCP 传输配置；分帧与应答关联由协议设定，保持默认值即可。</summary>
    public TcpClientChannelConfig Tcp { get; set; } = new TcpClientChannelConfig();

    /// <summary>S7 选项：连接类型、Rack、槽号与数据格式；默认 PG、Rack 0、槽 1、ABCD 字序。</summary>
    public S7ClientOptions Options { get; set; } = new S7ClientOptions();
}
