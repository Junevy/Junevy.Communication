namespace Junevy.Communication.Mqtt.Tests.TestSupport;

using MQTTnet;
using MQTTnet.Protocol;
using MQTTnet.Server;

/// <summary>
/// <see cref="TestBroker"/> 的场景开关与副作用操作：凭据校验、延迟 CONNECT 的应答、拒绝发布 / 订阅、
/// 从 Broker 侧注入消息、强制断开客户端。放在单独的文件里是为了让两个部分各自都在行数上限之内。
/// </summary>
public sealed partial class TestBroker
{
    /// <summary>要求 CONNECT 带上给定的凭据；不符时以 <c>BadUserNameOrPassword</c> 拒绝。</summary>
    /// <param name="userName">期望的用户名。</param>
    /// <param name="password">期望的密码。</param>
    /// <returns>同一个 Broker。</returns>
    public TestBroker WithCredentials(string userName, string password)
    {
        ExpectedUserName = userName;
        ExpectedPassword = password;
        return this;
    }

    /// <summary>把 CONNECT 的应答延迟一段时间，用于验证"连接进行中调用 DisconnectAsync"。</summary>
    /// <param name="delay">延迟时长。</param>
    /// <returns>同一个 Broker。</returns>
    public TestBroker DelayConnectAck(TimeSpan delay)
    {
        connectDelay = delay;
        return this;
    }

    /// <summary>拒绝下一次发布：PUBACK 带上失败原因码（仅 MQTT 5 有效）。</summary>
    /// <param name="reasonCode">要返回的原因码；仅下一次生效。</param>
    /// <returns>同一个 Broker。</returns>
    public TestBroker RejectNextPublish(MqttPubAckReasonCode reasonCode)
    {
        publishRejection = reasonCode;
        return this;
    }

    /// <summary>拒绝对某个主题的订阅（仅 MQTT 5 有效），仅下一次生效。</summary>
    /// <param name="topicFilter">要被拒绝的主题过滤器。</param>
    /// <param name="reasonCode">要返回的原因码。</param>
    /// <returns>同一个 Broker。</returns>
    public TestBroker RejectSubscription(
        string topicFilter,
        MqttSubscribeReasonCode reasonCode = MqttSubscribeReasonCode.TopicFilterInvalid)
    {
        rejectedTopic = topicFilter;
        subscriptionRejection = reasonCode;
        return this;
    }

    /// <summary>把对某个主题的下一次 SUBSCRIBE 的应答延迟一段时间，用于制造"恢复订阅时超时"。仅下一次生效。</summary>
    /// <param name="topicFilter">要延迟的主题过滤器。</param>
    /// <param name="delay">延迟时长。</param>
    /// <returns>同一个 Broker。</returns>
    public TestBroker DelayNextSubscription(string topicFilter, TimeSpan delay)
    {
        delayedTopic = topicFilter;
        subscriptionDelay = delay;
        return this;
    }

    /// <summary>从 Broker 侧向主题注入一条消息。</summary>
    /// <param name="topic">主题。</param>
    /// <param name="payload">正文。</param>
    /// <param name="retain">是否为保留消息。</param>
    /// <param name="qos">QoS。</param>
    /// <returns>注入完成。</returns>
    public Task InjectAsync(
        string topic,
        string payload,
        bool retain = false,
        MqttQualityOfServiceLevel qos = MqttQualityOfServiceLevel.AtMostOnce)
    {
        MqttApplicationMessage message = new MqttApplicationMessageBuilder()
            .WithTopic(topic)
            .WithPayload(payload)
            .WithRetainFlag(retain)
            .WithQualityOfServiceLevel(qos)
            .Build();

        return server.InjectApplicationMessage(
            new InjectedMqttApplicationMessage(message),
            CancellationToken.None);
    }

    /// <summary>强制断开某个客户端。Broker 侧发起的断开，按 MQTT 的规定会发布该客户端的遗嘱。</summary>
    /// <param name="clientId">客户端标识。</param>
    /// <param name="reasonCode">DISCONNECT 的原因码；默认 <c>DisconnectWithWillMessage</c>（4）。</param>
    /// <returns>断开完成。</returns>
    public Task DisconnectClientAsync(
        string clientId,
        MqttDisconnectReasonCode reasonCode = MqttDisconnectReasonCode.DisconnectWithWillMessage) =>
        server.DisconnectClientAsync(
            clientId,
            new MqttServerClientDisconnectOptionsBuilder().WithReasonCode(reasonCode).Build());
}