using System.Text;

namespace Junevy.Communication.Mqtt;

/// <summary>
/// 一条 MQTT 消息。不可变：构造与设置 MQTT 5 属性时都会复制传入的字节与列表，因此宿主在构造之后修改自己的数组
/// 或列表不会改变这条消息（P61-D3）。收到的消息同样先复制正文再交给宿主——SDK 的缓冲区可能被复用。
/// </summary>
public sealed class MqttMessage
{
    private readonly byte[] payload;
    private readonly byte[] correlationData;
    private readonly IReadOnlyList<MqttUserProperty> userProperties;

    /// <summary>用字节正文创建消息。</summary>
    /// <param name="topic">发布主题；不能为 null，可以为空字符串之外的任何合法主题。</param>
    /// <param name="payload">正文；内容会被复制。</param>
    /// <exception cref="ArgumentNullException"><paramref name="topic"/> 为 null。</exception>
    public MqttMessage(string topic, ReadOnlyMemory<byte> payload)
    {
        ArgumentNullException.ThrowIfNull(topic);

        Topic = topic;
        this.payload = payload.ToArray();
        correlationData = Array.Empty<byte>();
        userProperties = Array.Empty<MqttUserProperty>();
    }

    /// <summary>用 UTF-8 文本创建消息。</summary>
    /// <param name="topic">发布主题；不能为 null。</param>
    /// <param name="payload">正文；按 UTF-8 编码，内容会被复制。</param>
    /// <exception cref="ArgumentNullException"><paramref name="topic"/> 或 <paramref name="payload"/> 为 null。</exception>
    public MqttMessage(string topic, string payload)
        : this(topic, Encode(payload))
    {
    }

    /// <summary>发布或接收的主题。</summary>
    public string Topic { get; }

    /// <summary>正文的只读视图，内容是构造时复制的副本。</summary>
    public ReadOnlyMemory<byte> Payload => payload;

    /// <summary>服务质量（QoS）级别，默认 <see cref="MqttQos.AtMostOnce"/>。</summary>
    public MqttQos Qos { get; init; } = MqttQos.AtMostOnce;

    /// <summary>是否为保留消息。</summary>
    public bool Retain { get; init; }

    /// <summary>MQTT 5 的 ContentType；在 3.1.1 连接上发布时返回 <c>InvalidRequest</c>，不发送（P61-D3）。</summary>
    public string? ContentType { get; init; }

    /// <summary>MQTT 5 的 ResponseTopic；在 3.1.1 连接上发布时返回 <c>InvalidRequest</c>，不发送。</summary>
    public string? ResponseTopic { get; init; }

    /// <summary>MQTT 5 的 CorrelationData；空表示没有，设置时复制。在 3.1.1 连接上发布时返回 <c>InvalidRequest</c>。</summary>
    public ReadOnlyMemory<byte> CorrelationData
    {
        get => correlationData;
        init => correlationData = value.ToArray();
    }

    /// <summary>MQTT 5 的 MessageExpiryInterval（秒）；null 表示不设置。在 3.1.1 连接上发布时返回 <c>InvalidRequest</c>。</summary>
    public uint? MessageExpiryInterval { get; init; }

    /// <summary>MQTT 5 的用户属性；默认空列表，设置时复制。在 3.1.1 连接上发布时返回 <c>InvalidRequest</c>。</summary>
    public IReadOnlyList<MqttUserProperty> UserProperties
    {
        get => userProperties;
        init => userProperties = value is null
            ? throw new ArgumentNullException(nameof(value))
            : value.ToArray();
    }

    /// <summary>按 UTF-8 解码正文。</summary>
    /// <returns>正文文本。</returns>
    public string GetPayloadAsString() => Encoding.UTF8.GetString(payload);

    /// <summary>返回主题与投递方式的摘要，不含正文（P61-D3：日志与错误消息都不泄漏正文）。</summary>
    /// <returns>形如 <c>plant/line1/temp (QoS 1, retain, 12 bytes)</c> 的可读形式。</returns>
    public override string ToString() =>
        $"{Topic} (QoS {(int)Qos}{(Retain ? ", retain" : string.Empty)}, {payload.Length} bytes)";

    private static ReadOnlyMemory<byte> Encode(string payload)
    {
        ArgumentNullException.ThrowIfNull(payload);
        return Encoding.UTF8.GetBytes(payload);
    }
}