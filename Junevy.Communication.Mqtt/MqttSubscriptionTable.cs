using System.Collections.Concurrent;

namespace Junevy.Communication.Mqtt;

/// <summary>
/// 订阅表（P61-D7）：记下"Broker 承认过的订阅"，是重连后恢复订阅的唯一依据。
/// <para>规则：成功的订阅记入，同一过滤器再订阅时更新 QoS；Broker 拒绝的**不记入**；
/// 退订成功后才移除。线程安全，发布 / 订阅 / 退订可以并发调用。</para>
/// </summary>
internal sealed class MqttSubscriptionTable
{
    private readonly ConcurrentDictionary<string, MqttQos> subscriptions = new(StringComparer.Ordinal);

    /// <summary>当前记住的订阅，按加入顺序无关（比较用）。</summary>
    public IReadOnlyCollection<KeyValuePair<string, MqttQos>> Entries => subscriptions.ToArray();

    /// <summary>订阅表是否为空。</summary>
    public bool IsEmpty => subscriptions.IsEmpty;

    /// <summary>订阅成功后记录；同一过滤器会更新 QoS。</summary>
    /// <param name="topicFilter">主题过滤器。</param>
    /// <param name="qos">请求的 QoS。</param>
    public void Add(string topicFilter, MqttQos qos) => subscriptions[topicFilter] = qos;

    /// <summary>退订成功后移除。</summary>
    /// <param name="topicFilter">主题过滤器。</param>
    public void Remove(string topicFilter) => subscriptions.TryRemove(topicFilter, out _);

    /// <summary>重连恢复时某个过滤器被 Broker 拒绝，把它移出表，避免每次重连都重试同一个坏订阅。</summary>
    /// <param name="topicFilter">主题过滤器。</param>
    public void Forget(string topicFilter) => subscriptions.TryRemove(topicFilter, out _);
}