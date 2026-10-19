using System.Globalization;
using System.Text;
using Junevy.Communication.DebugAssistant.Common;
using Junevy.Communication.DebugAssistant.Sessions;
using Junevy.Communication.Mqtt;

namespace Junevy.Communication.DebugAssistant.Protocols.Mqtt;

/// <summary>MQTT 页的纯逻辑：状态映射、输入解析、消息组装与显示文字，不依赖界面，便于单元测试。</summary>
public static class MqttSupport
{
    public static string StateName(MqttConnectionState state) => state switch
    {
        MqttConnectionState.Disconnected => "未连接",
        MqttConnectionState.Connecting => "连接中",
        MqttConnectionState.Connected => "已连接",
        MqttConnectionState.Reconnecting => "重连中",
        _ => state.ToString(),
    };

    /// <summary>
    /// 库的状态映射到页签状态。没有原因的断开是未连接；带原因的断开（自动重连关闭后的意外断线）是故障，
    /// 页签保留原因，并允许重新连接。
    /// </summary>
    public static SessionState ToSessionState(MqttConnectionState state, string? reason) => state switch
    {
        MqttConnectionState.Connecting => SessionState.Connecting,
        MqttConnectionState.Connected => SessionState.Connected,
        MqttConnectionState.Reconnecting => SessionState.Reconnecting,
        _ => string.IsNullOrEmpty(reason) ? SessionState.NotConnected : SessionState.Faulted,
    };

    public static string QosText(MqttQos qos) => $"QoS {(int)qos}";

    /// <summary>负载：文本按 UTF-8 编码，不做转义（路径里的反斜杠保持原样）；十六进制按 <see cref="HexInput"/> 解析。</summary>
    public static byte[] ParsePayload(string text, bool asHex) => asHex ? HexInput.Parse(text) : Encoding.UTF8.GetBytes(text);

    /// <summary>可选的十六进制（CorrelationData）：空白表示没有。</summary>
    public static byte[]? ParseOptionalHex(string text) => text.Trim().Length == 0 ? null : HexInput.Parse(text);

    /// <summary>可选的消息过期间隔（秒）：空白表示不设置。</summary>
    public static uint? ParseExpiry(string text)
    {
        string trimmed = text.Trim();
        if (trimmed.Length == 0)
        {
            return null;
        }

        return uint.TryParse(trimmed, NumberStyles.None, CultureInfo.InvariantCulture, out uint seconds)
            ? seconds
            : throw new FormatException("消息过期间隔必须是非负整数（秒）。");
    }

    /// <summary>组装要发布的消息。空字符串的文本属性视为未设置。</summary>
    public static MqttMessage Build(
        string topic,
        byte[] payload,
        MqttQos qos,
        bool retain,
        string contentType,
        string responseTopic,
        byte[]? correlationData,
        uint? expiry,
        IReadOnlyList<MqttUserProperty> userProperties) => new(topic, payload)
    {
        Qos = qos,
        Retain = retain,
        ContentType = contentType.Length == 0 ? null : contentType,
        ResponseTopic = responseTopic.Length == 0 ? null : responseTopic,
        CorrelationData = correlationData is null ? ReadOnlyMemory<byte>.Empty : correlationData,
        MessageExpiryInterval = expiry,
        UserProperties = userProperties,
    };

    /// <summary>接收的消息的 MQTT 5 属性，逐项用 "名称=值" 分号分隔；没有属性时为空字符串。</summary>
    public static string DescribeProperties(MqttMessage message)
    {
        var parts = new List<string>();
        if (message.ContentType is { } contentType)
        {
            parts.Add($"ContentType={contentType}");
        }

        if (message.ResponseTopic is { } responseTopic)
        {
            parts.Add($"ResponseTopic={responseTopic}");
        }

        if (message.CorrelationData.Length > 0)
        {
            parts.Add($"CorrelationData={HexText(message.CorrelationData.Span)}");
        }

        if (message.MessageExpiryInterval is { } expiry)
        {
            parts.Add($"Expiry={expiry}s");
        }

        parts.AddRange(message.UserProperties.Select(property => property.ToString()));
        return string.Join("; ", parts);
    }

    /// <summary>字节的十六进制文字，两位一组，以空格分隔。</summary>
    public static string HexText(ReadOnlySpan<byte> bytes)
    {
        var builder = new StringBuilder(bytes.Length * 3);
        for (int i = 0; i < bytes.Length; i++)
        {
            if (i > 0)
            {
                builder.Append(' ');
            }

            builder.Append(bytes[i].ToString("X2", CultureInfo.InvariantCulture));
        }

        return builder.ToString();
    }
}
