using System.Diagnostics;
using Junevy.Communication.Core.Results;
using Junevy.Communication.Mqtt.Sdk;
using Microsoft.Extensions.Logging;

namespace Junevy.Communication.Mqtt;

/// <summary>
/// 发布、订阅与退订（P61-D6、D7）。与生命周期分开，是因为它们的并发规则不同：
/// 这三个方法**不**走生命周期锁，SDK 保证它们线程安全，因此可以并发调用。
/// </summary>
internal sealed class MqttMessaging
{
    private readonly MqttSdkClient sdk;
    private readonly MqttConnectionStateMachine state;
    private readonly MqttSubscriptionTable subscriptions;
    private readonly MqttProtocolVersion protocolVersion;
    private readonly ILogger logger;

    public MqttMessaging(
        MqttSdkClient sdk,
        MqttConnectionStateMachine state,
        MqttSubscriptionTable subscriptions,
        MqttProtocolVersion protocolVersion,
        ILogger logger)
    {
        this.sdk = sdk;
        this.state = state;
        this.subscriptions = subscriptions;
        this.protocolVersion = protocolVersion;
        this.logger = logger;
    }

    /// <summary>发布一条消息；QoS 0 写出即成功，QoS 1 等 PUBACK，QoS 2 等 PUBCOMP。</summary>
    /// <param name="message">本库消息。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>发布结果。</returns>
    public async Task<CommResult> PublishAsync(MqttMessage message, CancellationToken cancellationToken)
    {
        if (!MqttSdkMessages.TryValidateForPublish(message, protocolVersion, out string? invalid))
        {
            return MqttErrorMap.InvalidRequest($"The message cannot be published: {invalid}");
        }

        if (state.Current != MqttConnectionState.Connected)
        {
            return NotConnected("published");
        }

        Stopwatch timer = Stopwatch.StartNew();
        CommResult outcome = await sdk.PublishAsync(message, cancellationToken).ConfigureAwait(false);
        timer.Stop();
        Log(outcome, "Publish", message.Topic, message.Payload.Length, timer.ElapsedMilliseconds);

        return outcome;
    }

    /// <summary>订阅一个主题过滤器；成功的订阅记入订阅表。</summary>
    /// <param name="topicFilter">主题过滤器。</param>
    /// <param name="qos">请求的 QoS。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>Broker 授予的 QoS，或失败原因。</returns>
    public async Task<CommResult<MqttQos>> SubscribeAsync(string topicFilter, MqttQos qos, CancellationToken cancellationToken)
    {
        if (!MqttSdkTopics.TryValidateSubscribeTopic(topicFilter, out string? invalid) || !MqttSdkQos.IsDefined(qos))
        {
            string reason = invalid ?? $"the QoS value {(int)qos} is not defined.";
            return MqttErrorMap.InvalidRequest($"The subscription is not valid: {reason}").As<MqttQos>();
        }

        if (state.Current != MqttConnectionState.Connected)
        {
            return NotConnected("subscribed").As<MqttQos>();
        }

        Stopwatch timer = Stopwatch.StartNew();
        CommResult<MqttQos> outcome = await sdk.SubscribeAsync(topicFilter, qos, cancellationToken).ConfigureAwait(false);
        timer.Stop();
        Log(outcome.ToResult(), "Subscribe", topicFilter, 1, timer.ElapsedMilliseconds);

        // Broker 拒绝的订阅不进表（P61-D7）。
        if (outcome.IsSuccess)
        {
            subscriptions.Add(topicFilter, qos);
        }

        return outcome;
    }

    /// <summary>退订一个主题过滤器；成功后才从订阅表移除。</summary>
    /// <param name="topicFilter">主题过滤器。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>退订结果。</returns>
    public async Task<CommResult> UnsubscribeAsync(string topicFilter, CancellationToken cancellationToken)
    {
        if (!MqttSdkTopics.TryValidateSubscribeTopic(topicFilter, out string? invalid))
        {
            return MqttErrorMap.InvalidRequest($"The subscription is not valid: {invalid}");
        }

        if (state.Current != MqttConnectionState.Connected)
        {
            return NotConnected("unsubscribed");
        }

        Stopwatch timer = Stopwatch.StartNew();
        CommResult outcome = await sdk.UnsubscribeAsync(topicFilter, cancellationToken).ConfigureAwait(false);
        timer.Stop();
        Log(outcome, "Unsubscribe", topicFilter, 1, timer.ElapsedMilliseconds);

        if (outcome.IsSuccess)
        {
            subscriptions.Remove(topicFilter);
        }

        return outcome;
    }

    /// <summary>当前记住的订阅（P61-D7）：过滤器 → 请求的 QoS。重连恢复时按这个表逐个重新订阅。</summary>
    public IReadOnlyCollection<KeyValuePair<string, MqttQos>> Subscriptions => subscriptions.Entries;

    /// <summary>
    /// 重连成功后按订阅表逐个重新订阅（P61-D9）。
    /// <para>调用时机是"已经连上、但状态还没报成 <see cref="MqttConnectionState.Connected"/>"，因此这里直接走 SDK，
    /// 不经过上面那个"必须是 Connected"的守卫——宿主看到 <c>Connected</c> 时订阅已经可用（P61-D5）。</para>
    /// <para>某个过滤器被 Broker 拒绝时记一条 Warning 并把它移出订阅表，避免每次重连都重试同一个坏订阅。</para>
    /// </summary>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>成功恢复的订阅数量。</returns>
    public async Task<int> RestoreSubscriptionsAsync(CancellationToken cancellationToken)
    {
        int restored = 0;

        foreach (KeyValuePair<string, MqttQos> entry in subscriptions.Entries.ToArray())
        {
            CommResult<MqttQos> outcome = await sdk.SubscribeAsync(entry.Key, entry.Value, cancellationToken).ConfigureAwait(false);
            if (outcome.IsSuccess)
            {
                restored++;
                continue;
            }

            logger.LogWarning(
                "The broker rejected the restored subscription on {Topic}: {Outcome}. The topic is forgotten.",
                entry.Key,
                outcome);
            subscriptions.Forget(entry.Key);
        }

        return restored;
    }

    private static CommResult NotConnected(string pastTense) => CommResult.Fail(
        $"The MQTT client is not connected, so the request cannot be {pastTense}.",
        CommErrorKind.NotConnected);

    // 每次操作记 Debug（含主题、字节数或数量、耗时），失败记 Warning（P6-C10）。只记摘要，不记正文。
    private void Log(CommResult outcome, string operation, string subject, int count, long elapsedMilliseconds)
    {
        if (outcome.IsSuccess)
        {
            logger.LogDebug(
                "{Operation} on {Subject} ({Count} unit(s)) completed in {Elapsed} ms.",
                operation,
                subject,
                count,
                elapsedMilliseconds);
            return;
        }

        logger.LogWarning(
            "{Operation} on {Subject} failed after {Elapsed} ms: {Outcome}.",
            operation,
            subject,
            elapsedMilliseconds,
            outcome);
    }
}