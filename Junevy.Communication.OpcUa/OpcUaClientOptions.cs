namespace Junevy.Communication.OpcUa;

/// <summary>OPC UA 客户端选项。全部是简单属性，可直接从 appsettings 绑定；<c>Create</c> 时校验并取快照。</summary>
public sealed class OpcUaClientOptions
{
    /// <summary>
    /// 证书与信任列表的根目录，默认 <c>%LocalAppData%\Junevy.Communication\OpcUa\pki</c>。
    /// <c>None</c> 安全策略下客户端不需要证书，因此本库**不会**在这里生成自签名证书；
    /// 该目录只是配置里必需的存储位置（P62-D4，调研结论见计划第 19.2.14 节）。
    /// </summary>
    public static string DefaultPkiRoot => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Junevy.Communication",
        "OpcUa",
        "pki");

    /// <summary>服务器端点地址，必填，形式为 <c>opc.tcp://host:port[/path]</c>。</summary>
    public string? EndpointUrl { get; set; }

    /// <summary>本客户端的应用名，默认 <c>Junevy.Communication.OpcUa</c>。</summary>
    public string ApplicationName { get; set; } = "Junevy.Communication.OpcUa";

    /// <summary>本客户端的应用 URI；为 null 时按 <c>urn:{机器名}:{ApplicationName}</c> 生成。</summary>
    public string? ApplicationUri { get; set; }

    /// <summary>证书与信任列表的根目录，见 <see cref="DefaultPkiRoot"/>。</summary>
    public string PkiRoot { get; set; } = DefaultPkiRoot;

    /// <summary>发现端点与创建会话的超时，毫秒。</summary>
    public int ConnectTimeout { get; set; } = 10000;

    /// <summary>每个服务调用的超时，毫秒。</summary>
    public int OperationTimeout { get; set; } = 10000;

    /// <summary>向服务器申请的会话超时，毫秒。</summary>
    public int SessionTimeout { get; set; } = 60000;

    /// <summary>会话保活间隔，毫秒；用来发现断线。</summary>
    public int KeepAliveInterval { get; set; } = 5000;

    /// <summary>重连的初始间隔，毫秒。</summary>
    public int ReconnectInterval { get; set; } = 1000;

    /// <summary>重连间隔的上限，毫秒。</summary>
    public int MaxReconnectInterval { get; set; } = 30000;
}
