namespace Junevy.Communication.OpcUa;

/// <summary>
/// 一个已建立的订阅（P62-D10）。<see cref="DisposeAsync"/> 尽力删除服务器上的订阅，可以重复调用。
/// </summary>
public sealed class OpcUaSubscription : IAsyncDisposable
{
    private readonly Func<ValueTask> delete;
    private int disposed;

    /// <summary>
    /// 构造订阅句柄。
    /// </summary>
    /// <param name="nodeIds">订阅时传入的 NodeId 文本，与 <see cref="ItemStatuses"/> 同序。</param>
    /// <param name="itemStatuses">每个监控项的创建状态，与 <see cref="NodeIds"/> 同序。</param>
    /// <param name="delete">删除服务器上订阅的回调（尽力而为）。</param>
    public OpcUaSubscription(
        IReadOnlyList<string> nodeIds,
        IReadOnlyList<OpcUaStatusCode> itemStatuses,
        Func<ValueTask> delete)
    {
        NodeIds = nodeIds;
        ItemStatuses = itemStatuses;
        this.delete = delete;
    }

    /// <summary>订阅时传入的 NodeId 文本，与 <see cref="ItemStatuses"/> 同序。</summary>
    public IReadOnlyList<string> NodeIds { get; }

    /// <summary>
    /// 每个监控项的创建状态，与 <see cref="NodeIds"/> 同序。某一项 Bad 不影响其余项，也不让整个调用失败。
    /// </summary>
    public IReadOnlyList<OpcUaStatusCode> ItemStatuses { get; }

    /// <summary>
    /// 删除服务器上的订阅（尽力而为）。可以重复调用；断开或释放之后调用不抛异常。
    /// </summary>
    /// <returns>异步释放。</returns>
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0)
        {
            return;
        }

        await delete().ConfigureAwait(false);
    }
}
