using Junevy.Communication.Mqtt;

namespace Junevy.Communication.DebugAssistant.Protocols.Mqtt;

/// <summary>一个已订阅的主题过滤器：请求的 QoS 与 Broker 授予的 QoS（计划 7.7 节要求显示授予的 QoS）。</summary>
public sealed record MqttSubscriptionRow(string TopicFilter, MqttQos RequestedQos, MqttQos GrantedQos)
{
    public string RequestedText => MqttSupport.QosText(RequestedQos);

    public string GrantedText => MqttSupport.QosText(GrantedQos);
}

/// <summary>发布时的一项 MQTT 5 用户属性（名称可以重复，MQTT 允许）。</summary>
public sealed record MqttUserPropertyRow(string Name, string Value);

/// <summary>接收列表的一行，字段都已经是界面要显示的文字。</summary>
public sealed record MqttReceivedRow(
    string Time,
    string Topic,
    string Qos,
    string Retain,
    string PayloadText,
    string PayloadHex,
    string Properties);
