namespace Junevy.Communication.OpcUa;

/// <summary>订阅选项。只影响监控项的创建方式，不改变通知的语义。</summary>
public sealed class OpcUaSubscriptionOptions
{
    /// <summary>发布间隔，毫秒。</summary>
    public int PublishingInterval { get; set; } = 1000;

    /// <summary>采样间隔，毫秒；为 null 时等于 <see cref="PublishingInterval"/>，为 0 表示服务器支持的最快速度。</summary>
    public int? SamplingInterval { get; set; }

    /// <summary>每个监控项的队列长度。</summary>
    public uint QueueSize { get; set; } = 1;

    /// <summary>队列满时是否丢弃最旧的值。</summary>
    public bool DiscardOldest { get; set; } = true;
}
