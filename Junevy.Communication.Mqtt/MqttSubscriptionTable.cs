using System.Collections.Concurrent;

namespace Junevy.Communication.Mqtt;

/// <summary>
/// 订阅表（P61-D7）：记下"Broker 承认过的订阅"，是重连后恢复订阅的唯一依据。
/// <para>规则：成功的订阅记入，同一过滤器再订阅时**只更新 QoS、不改顺序**（顺序是首次订阅的顺序）；
/// Broker 拒绝的**不记入**；退订成功后才移除。</para>
/// <para>为什么自己维护顺序：重连要"逐个"重新订阅（P61-D9），顺序必须可复现，
/// 而 <see cref="ConcurrentDictionary{TKey,TValue}"/> 的枚举顺序没有保证。</para>
/// </summary>
internal sealed class MqttSubscriptionTable
{
    private readonly ConcurrentDictionary<string, Entry> entries = new(StringComparer.Ordinal);
    private long sequence;

    /// <summary>当前记住的订阅，按首次订阅的顺序；每次读取都是新的快照，遍历期间可以修改表。</summary>
    public IReadOnlyList<KeyValuePair<string, MqttQos>> Entries =>
        entries.Values.OrderBy(entry => entry.Order).Select(entry => new KeyValuePair<string, MqttQos>(entry.Filter, entry.Qos)).ToArray();

    /// <summary>订阅成功后记录；同一过滤器只更新 QoS，保留原来的位置。</summary>
    /// <param name="topicFilter">主题过滤器。</param>
    /// <param name="qos">请求的 QoS。</param>
    public void Add(string topicFilter, MqttQos qos)
    {
        entries.AddOrUpdate(
            topicFilter,
            _ => new Entry(topicFilter, qos, Interlocked.Increment(ref sequence)),
            (_, existing) => existing with { Qos = qos });
    }

    /// <summary>退订成功后移除。</summary>
    /// <param name="topicFilter">主题过滤器。</param>
    public void Remove(string topicFilter) => entries.TryRemove(topicFilter, out _);

    /// <summary>重连恢复时某个过滤器被 Broker 明确拒绝，把它移出表，避免每次重连都重试同一个坏订阅。</summary>
    /// <param name="topicFilter">主题过滤器。</param>
    public void Forget(string topicFilter) => entries.TryRemove(topicFilter, out _);

    private sealed record Entry(string Filter, MqttQos Qos, long Order);
}