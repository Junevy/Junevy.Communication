using Junevy.Communication.Core.Diagnostics;
using Junevy.Communication.Core.Results;
using Junevy.Communication.Core.Utils;
using Microsoft.Extensions.Logging;
using Opc.Ua;
using Opc.Ua.Client;
using SdkSubscription = Opc.Ua.Client.Subscription;

namespace Junevy.Communication.OpcUa.Sdk;

/// <summary>
/// 一个订阅的 SDK 侧实现：创建监控项、等待创建结果、把数据变更通知交给调用方、删除（P62-D10）。
/// 重连时的订阅转移交给 <c>ManagedSession</c>（<c>WithTransferSubscriptionsOnRecreate</c>），这里不写重连逻辑。
/// </summary>
internal sealed class OpcUaSdkSubscription
{
    private readonly Request request;
    private readonly SdkSubscription subscription;
    private readonly MonitoredItem[] items;

    private OpcUaSdkSubscription(Request request, SdkSubscription subscription, MonitoredItem[] items)
    {
        this.request = request;
        this.subscription = subscription;
        this.items = items;
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

    /// <summary>
    /// 建立订阅。整个过程最多一个 <c>OperationTimeout</c>（P62-D10）；失败时把已经建出来的部分清理掉。
    /// **监控项的创建是异步的**（F15），因此返回前要等到每一项已建好或者已经有错误。
    /// </summary>
    /// <param name="request">这个订阅不变的上下文。</param>
    /// <param name="nodeIds">已解析的节点。</param>
    /// <param name="texts">与 <paramref name="nodeIds"/> 同序的 NodeId 文本，通知与逐项状态都用它。</param>
    /// <param name="options">已校验的订阅选项。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>订阅；逐项创建状态在 <see cref="ItemStatuses"/>。</returns>
    public static async Task<CommResult<OpcUaSdkSubscription>> CreateAsync(
        Request request,
        IReadOnlyList<NodeId> nodeIds,
        IReadOnlyList<string> texts,
        OpcUaSubscriptionOptions options,
        CancellationToken cancellationToken)
    {
        var subscription = new SdkSubscription(request.Telemetry, new SubscriptionOptions
        {
            // 2.0 的 SubscriptionOptions.PublishingEnabled 默认是 false，不显式打开就一个通知都不会来（F28）。
            PublishingEnabled = true,
            PublishingInterval = options.PublishingInterval,
            TimestampsToReturn = TimestampsToReturn.Both,
        });

        var items = new MonitoredItem[nodeIds.Count];
        for (int i = 0; i < nodeIds.Count; i++)
        {
            string text = texts[i];
            var item = new MonitoredItem(request.Telemetry, new MonitoredItemOptions
            {
                StartNodeId = nodeIds[i],
                AttributeId = Attributes.Value,
                SamplingInterval = options.SamplingInterval ?? options.PublishingInterval,
                QueueSize = options.QueueSize,
                DiscardOldest = options.DiscardOldest,
                DisplayName = text,
            });

            item.Notification += (_, args) => request.Deliver(text, args);
            items[i] = item;
            subscription.AddItem(item);
        }

        using var scope = TimeoutScope.Start(request.OperationTimeout, cancellationToken);
        try
        {
            // 顺序不能换：CreateAsync 要先确认订阅已经挂在会话上，否则直接抛异常。
            request.Session.AddSubscription(subscription);
            await subscription.CreateAsync(scope.Token).ConfigureAwait(false);
            await WaitForItemsAsync(items, scope.Token).ConfigureAwait(false);

            return CommResult<OpcUaSdkSubscription>.Success(new OpcUaSdkSubscription(request, subscription, items));
        }
        catch (Exception exception)
        {
            // 已经挂到会话上的订阅必须摘下来，否则会话恢复时 SDK 会把这个没建成的订阅在服务器上建出来（F30）。
            await new OpcUaSdkSubscription(request, subscription, items).DeleteAsync().ConfigureAwait(false);
            return OpcUaSdkErrorMap.Runtime(exception, scope).As<OpcUaSdkSubscription>();
        }
    }

    /// <summary>
    /// 停止投递通知，从会话上摘下订阅并删除服务器上的订阅。尽力而为、不抛异常。
    /// 会话正常时调用方最多等一个 <c>OperationTimeout</c>；重连期间立刻返回，摘除在会话恢复之后由后台完成。
    /// </summary>
    /// <returns>异步删除。</returns>
    public async Task DeleteAsync()
    {
        foreach (MonitoredItem item in items)
        {
            item.DetachNotificationEventHandlers();
        }

        // 摘除不能取消：取消之后订阅还挂在会话上，会话恢复时会被重建；要是再把它释放掉，
        // 之后每一次重连都会因为"启动已释放订阅的保活计时器"而失败（F32）。
        Task removing = RemoveAndDisposeAsync();
        if (!request.Session.IsUsable)
        {
            request.Logger.LogInformation(
                "The OPC UA session is reconnecting; the subscription is removed once the session recovers.");
            return;
        }

        try
        {
            await removing.WaitAsync(TimeSpan.FromMilliseconds(request.OperationTimeout)).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            request.Logger.LogInformation(
                "Deleting the OPC UA subscription is taking longer than the operation timeout; it continues in the background.");
        }
    }

    // 只有摘除成功才释放 SDK 的订阅对象（见 DeleteAsync）。本方法不抛异常：它可能在后台被丢下，没有人观察。
    private async Task RemoveAndDisposeAsync()
    {
        try
        {
            // 重连期间不能先把摘除发出去：SDK 在会话重新激活的那一刻就发出这次删除，订阅还拿着旧的订阅号，
            // 服务器回 BadSubscriptionIdInvalid，订阅留在会话上随后被重建（F32）。等恢复完成再摘。
            await request.Session.WaitUntilConnectedAsync().ConfigureAwait(false);
            await request.Session.RemoveSubscriptionAsync(subscription, CancellationToken.None).ConfigureAwait(false);
            subscription.Dispose();
        }
        catch (Exception exception)
        {
            // 断开或释放之后再删订阅会抛"对象已释放"：会话已经没了，订阅随它一起作废（P62-D10：不抛异常）。
            request.Logger.LogWarning(exception, "Deleting the OPC UA subscription failed; continuing.");
        }
    }

    private static OpcUaStatusCode StatusOf(MonitoredItem item)
    {
        ServiceResult? error = item.Status.Error;
        return item.Status.Created || error is null ? default : new OpcUaStatusCode(error.StatusCode.Code);
    }

    private static async Task WaitForItemsAsync(MonitoredItem[] items, CancellationToken cancellationToken)
    {
        foreach (MonitoredItem item in items)
        {
            while (!item.Status.Created && item.Status.Error is null)
            {
                await Task.Delay(10, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    /// <summary>一个订阅不变的上下文：会话、遥测、日志、超时，以及通知的 sender 与处理器。</summary>
    internal sealed class Request
    {
        private readonly object owner;
        private readonly EventHandler<OpcUaDataChangedEventArgs> handler;

        /// <summary>
        /// 构造上下文。
        /// </summary>
        /// <param name="session">承载订阅的会话。</param>
        /// <param name="telemetry">遥测上下文。</param>
        /// <param name="logger">本包的日志器。</param>
        /// <param name="operationTimeout">每次与服务器往来的超时（毫秒）。</param>
        /// <param name="owner">客户端本身：数据变更事件的 sender。</param>
        /// <param name="handler">数据变更处理器。</param>
        public Request(
            OpcUaSdkSession session,
            ITelemetryContext telemetry,
            ILogger logger,
            int operationTimeout,
            object owner,
            EventHandler<OpcUaDataChangedEventArgs> handler)
        {
            Session = session;
            Telemetry = telemetry;
            Logger = logger;
            OperationTimeout = operationTimeout;
            this.owner = owner;
            this.handler = handler;
        }

        /// <summary>承载订阅的会话。</summary>
        public OpcUaSdkSession Session { get; }

        /// <summary>遥测上下文。</summary>
        public ITelemetryContext Telemetry { get; }

        /// <summary>本包的日志器。</summary>
        public ILogger Logger { get; }

        /// <summary>每次与服务器往来的超时（毫秒）。</summary>
        public int OperationTimeout { get; }

        /// <summary>
        /// 交付一条通知。按事件的约定逐个调用处理器的订阅者：某个订阅者抛异常只记一条 Error，
        /// 其余订阅者与后续通知照常（P62-D10，<see cref="EventSubscribers.InvokeEach{TArgs}"/>）。
        /// </summary>
        /// <param name="nodeId">订阅时传入的 NodeId 文本。</param>
        /// <param name="args">SDK 的通知。</param>
        public void Deliver(string nodeId, MonitoredItemNotificationEventArgs args)
        {
            // 通知值是 IEncodeable，里面装的是 MonitoredItemNotification，真正的 DataValue 在它的 Value 上（F27）。
            // 事件型监控项装的是别的东西，本期只做数据变更，忽略即可。
            if (args.NotificationValue is not MonitoredItemNotification notification)
            {
                return;
            }

            EventSubscribers.InvokeEach(
                handler,
                owner,
                new OpcUaDataChangedEventArgs(nodeId, OpcUaSdkValues.FromSdk(notification.Value)),
                Logger,
                "OpcUaClient.SubscribeAsync handler for " + nodeId);
        }
    }
}
