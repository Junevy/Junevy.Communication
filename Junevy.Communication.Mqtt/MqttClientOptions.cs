namespace Junevy.Communication.Mqtt;

/// <summary>
/// 客户端的连接选项。全为简单属性，可以直接由 appsettings 绑定；<see cref="MqttClient.Create"/> 时校验并取快照，
/// 之后修改本对象不影响已创建的客户端。
/// </summary>
public sealed class MqttClientOptions
{
    /// <summary>Broker 的主机名或 IP；必填。</summary>
    public string? Host { get; set; }

    /// <summary>Broker 的端口；默认 1883，使用 TLS 时通常由宿主改成 8883。</summary>
    public int Port { get; set; } = 1883;

    /// <summary>客户端标识；必填。同一 Broker 上必须唯一，否则第二个连接会被踢掉。</summary>
    public string? ClientId { get; set; }

    /// <summary>用户名；给了 <see cref="Password"/> 时必填。</summary>
    public string? UserName { get; set; }

    /// <summary>密码；非 null 时 <see cref="UserName"/> 也必须给出。任何级别的日志都不记录它。</summary>
    public string? Password { get; set; }

    /// <summary>协议版本；默认 <see cref="MqttProtocolVersion.V311"/>。</summary>
    public MqttProtocolVersion ProtocolVersion { get; set; } = MqttProtocolVersion.V311;

    /// <summary>Clean Session：3.1.1 的 Clean Session 与 5.0 的 Clean Start。false 表示会话需要被 Broker 保留。</summary>
    public bool CleanSession { get; set; } = true;

    /// <summary>会话过期时间（秒）；只在 5.0 下可以非零。</summary>
    public uint SessionExpiryInterval { get; set; }

    /// <summary>保活周期（秒），0–65535；0 表示不发 PINGREQ。默认 15。</summary>
    public int KeepAliveSeconds { get; set; } = 15;

    /// <summary>连接超时与每一条需要确认的报文的等待时间（毫秒）；默认 10000。</summary>
    public int Timeout { get; set; } = 10000;

    /// <summary>是否使用 TLS。</summary>
    public bool UseTls { get; set; }

    /// <summary>是否接受任何证书与主机名不符的服务器证书。置为 true 时 <see cref="MqttClient.Create"/> 记一条 Warning。</summary>
    public bool AllowUntrustedCertificates { get; set; }

    /// <summary>意外断开后是否自动重连并恢复订阅；默认 true（P6-C11：会话恢复是 MQTT 的原生语义）。</summary>
    public bool AutoReconnect { get; set; } = true;

    /// <summary>第一次重连前的等待（毫秒）；默认 1000，之后按 2 倍退避到 <see cref="MaxReconnectInterval"/>。</summary>
    public int ReconnectInterval { get; set; } = 1000;

    /// <summary>退避上限（毫秒）；默认 30000，必须不小于 <see cref="ReconnectInterval"/>。</summary>
    public int MaxReconnectInterval { get; set; } = 30000;

    /// <summary>遗嘱消息的主题；null 表示不设遗嘱（P61-D14）。</summary>
    public string? WillTopic { get; set; }

    /// <summary>遗嘱消息的正文（UTF-8 文本）；null 表示空正文。</summary>
    public string? WillPayload { get; set; }

    /// <summary>遗嘱消息的质量-of-服务级别；默认 <see cref="MqttQos.AtMostOnce"/>。</summary>
    public MqttQos WillQos { get; set; } = MqttQos.AtMostOnce;

    /// <summary>遗嘱消息是否为保留消息。</summary>
    public bool WillRetain { get; set; }

    /// <summary>
    /// 复制一份选项。创建客户端时取快照用——宿主之后修改原对象不影响已创建的客户端（P6-C9）。
    /// </summary>
    /// <returns>内容相同的独立副本。</returns>
    internal MqttClientOptions Copy() => new MqttClientOptions
    {
        Host = Host,
        Port = Port,
        ClientId = ClientId,
        UserName = UserName,
        Password = Password,
        ProtocolVersion = ProtocolVersion,
        CleanSession = CleanSession,
        SessionExpiryInterval = SessionExpiryInterval,
        KeepAliveSeconds = KeepAliveSeconds,
        Timeout = Timeout,
        UseTls = UseTls,
        AllowUntrustedCertificates = AllowUntrustedCertificates,
        AutoReconnect = AutoReconnect,
        ReconnectInterval = ReconnectInterval,
        MaxReconnectInterval = MaxReconnectInterval,
        WillTopic = WillTopic,
        WillPayload = WillPayload,
        WillQos = WillQos,
        WillRetain = WillRetain,
    };
}