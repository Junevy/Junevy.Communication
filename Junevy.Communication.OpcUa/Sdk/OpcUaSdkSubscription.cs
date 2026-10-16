using Junevy.Communication.Core.Results;
using Microsoft.Extensions.Logging;
using Opc.Ua;
using Opc.Ua.Client;
using SdkSubscription = Opc.Ua.Client.Subscription;

namespace Junevy.Communication.OpcUa.Sdk;

/// <summary>
/// 一个订阅的 SDK 侧实现：创建监控项、等待创建结果、把数据变更通知交给调用方（P62-D10）。
/// 重连时的订阅转移交给 <c>ManagedSession</c>（<c>WithTransferSubscriptionsOnRecreate</c>），这里不写重连逻辑。
/// </summary>
internal sealed class OpcUaSdkSubscription : IAsyncDisposable
{
    private readonly SdkSubscription subscription;
    private readonly MonitoredItem[] items;
    private readonly string[] nodeIds;
    private readonly ILogger logger;
    private int disposed;

    private OpcUaSdkSubscription(
        SdkSubscription subscription,
        MonitoredItem[] items,
        string[] nodeIds,
        ILogger logger)
    {
        this.subscription = subscription;
        this.items = items;
        this.nodeIds = nodeIds;
        this.logger = logger;
    }

    /// <summary>
    /// 建立订阅。**监控项的创建是异步的**（F15），因此返回前要等到每一项
    /// <c>Created</c> 或者已经有 <c>Status</c>。
    /// </summary>
    /// <param name="session">已建立的会话。</param>
    /// <param name="telemetry">遥测上下文。</param>
    /// <param name="logger">本包的日志器。</param>
    /// <param name="nodeIds">已解析的节点。</param>
    /// <param name="texts">与 <paramref name="nodeIds"/> 同序的 NodeId 文本，回调与逐项状态都用它。</param>
    /// <param name="handler">数据变更处理器；在 SDK 的回调线程上逐条交付。</param>
    /// <param name="options">订阅选项。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>订阅；逐项创建状态在 <see cref="OpcUaSubscription.ItemStatuses"/>。</returns>
    public static async Task<CommResult<OpcUaSdkSubscription>> CreateAsync(
        OpcUaSdkSession session,
        ITelemetryContext telemetry,
        ILogger logger,
        IReadOnlyList<NodeId> nodeIds,
        IReadOnlyList<string> texts,
        EventHandler<OpcUaDataChangedEventArgs> handler,
        OpcUaSubscriptionOptions options,
        CancellationToken cancellationToken)
    {
        var sdkOptions = new SubscriptionOptions
        {
            // 2.0 的 SubscriptionOptions.PublishingEnabled 默认是 false，不显式打开就一个通知都不会来（F28）。
            PublishingEnabled = true,
            PublishingInterval = options.PublishingInterval,
            TimestampsToReturn = TimestampsToReturn.Both,
        };

        var subscription = new SdkSubscription(telemetry, sdkOptions);
        var items = new MonitoredItem[nodeIds.Count];
        var original = new string[nodeIds.Count];

        for (int i = 0; i < nodeIds.Count; i++)
        {
            original[i] = texts[i];

            var item = new MonitoredItem(
                telemetry,
                new MonitoredItemOptions
                {
                    StartNodeId = nodeIds[i],
                    AttributeId = Attributes.Value,
                    SamplingInterval = options.SamplingInterval ?? options.PublishingInterval,
                    QueueSize = options.QueueSize,
                    DiscardOldest = options.DiscardOldest,
                    DisplayName = original[i],
                });

            string nodeId = original[i];
            item.Notification += (_, args) => Deliver(handler, logger, nodeId, args);
            items[i] = item;
            subscription.AddItem(item);
        }

        try
        {
            // 顺序不能换：CreateAsync 要先确认订阅已经挂在会话上，否则直接抛异常。
            session.AddSubscription(subscription);
            await subscription.CreateAsync(cancellationToken).ConfigureAwait(false);
            await WaitForItemsAsync(items, cancellationToken).ConfigureAwait(false);

            return CommResult<OpcUaSdkSubscription>.Success(
                new OpcUaSdkSubscription(subscription, items, original, logger));
        }
        catch (Exception exception)
        {
            subscription.Dispose();
            return OpcUaSdkErrorMap.Runtime(exception).As<OpcUaSdkSubscription>();
        }
    }

    /// <summary>每项的创建状态，与 <see cref="OpcUaSubscription.NodeIds"/> 同序。</summary>
    public IReadOnlyList<OpcUaStatusCode> ItemStatuses
    {
        get
        {
            var statuses = new OpcUaStatusCode[items.Length];
            for (int i = 0; i < items.Length; i++)
            {
                statuses[i] = StatusOf(items[i]);
            }

            return statuses;
        }
    }

    /// <summary>逐项的状态（监控项创建失败时该项是 Bad，其余照常收通知）。</summary>
    /// <returns>状态码列表。</returns>
    public OpcUaStatusCode[] Statuses()
    {
        var statuses = new OpcUaStatusCode[items.Length];
        for (int i = 0; i < items.Length; i++)
        {
            statuses[i] = StatusOf(items[i]);
        }

        return statuses;
    }

    /// <summary>删除服务器上的订阅（可重复调用）。</summary>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>异步删除。</returns>
    public async Task DeleteAsync(CancellationToken cancellationToken)
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0)
        {
            return;
        }

        foreach (MonitoredItem item in items)
        {
            item.DetachNotificationEventHandlers();
        }

        try
        {
            await subscription.DeleteAsync(false, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            // 断开或释放之后再删订阅会抛"对象已释放"，这里只记一条警告（P62-D10：不抛异常）。
            logger.LogWarning(exception, "Deleting the OPC UA subscription failed; continuing.");
        }
    }

    /// <summary>释放 SDK 对象（可重复调用）。</summary>
    /// <returns>异步释放。</returns>
    public async ValueTask DisposeAsync()
    {
        if (Volatile.Read(ref disposed) != 0)
        {
            return;
        }

        await DeleteAsync(CancellationToken.None).ConfigureAwait(false);
    }

    private static void Deliver(
        EventHandler<OpcUaDataChangedEventArgs> handler,
        ILogger logger,
        string nodeId,
        MonitoredItemNotificationEventArgs args)
    {
        if (args.NotificationValue is null)
        {
            return;
        }

        // 通知值是 IEncodeable，里面装的是 MonitoredItemNotification，真正的 DataValue 在它的 Value 上（F27）。
        // 事件型监控项装的是别的东西，本期只做数据变更，忽略即可。
        if (args.NotificationValue is not MonitoredItemNotification notification)
        {
            return;
        }

        try
        {
            handler(handler, new OpcUaDataChangedEventArgs(nodeId, OpcUaSdkValues.FromSdk(notification.Value)));
        }
        catch (Exception exception)
        {
            // 处理器抛异常只记一条 Error，后续通知照常交付（P62-D10）。
            logger.LogError(exception, "The OPC UA data changed handler threw for {Node}; later notifications continue.", nodeId);
        }
    }

    private static OpcUaStatusCode StatusOf(MonitoredItem item)
    {
        ServiceResult? error = item.Status.Error;
        return item.Status.Created || error is null ? default : new OpcUaStatusCode(error.StatusCode.Code);
    }

    private static async Task WaitForItemsAsync(MonitoredItem[] items, CancellationToken cancellationToken)
    {
        for (int i = 0; i < items.Length; i++)
        {
            while (!items[i].Status.Created && items[i].Status.Error is null)
            {
                cancellationToken.ThrowIfCancellationRequested();
                await Task.Delay(10, cancellationToken).ConfigureAwait(false);
            }
        }
    }
}
