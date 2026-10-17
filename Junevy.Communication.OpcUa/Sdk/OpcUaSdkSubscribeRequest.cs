using Junevy.Communication.Core.Results;
using Microsoft.Extensions.Logging;
using Opc.Ua;

namespace Junevy.Communication.OpcUa.Sdk;

/// <summary>
/// 一个订阅请求的装配（P62-D10）：先把 NodeId 全部解析好，任何一项不合法就整批失败，
/// 再建 SDK 的订阅，最后包装成对外的 <see cref="OpcUaSubscription"/>。
/// 与读、写、浏览一样由 <see cref="OpcUaSdkConnection"/> 调用，不碰 SDK 会话的生命周期。
/// </summary>
internal static class OpcUaSdkSubscribeRequest
{
    /// <summary>
    /// 建一个订阅。
    /// </summary>
    /// <param name="session">已建立的会话，提供命名空间表并承载 SDK 的订阅。</param>
    /// <param name="telemetry">遥测上下文。</param>
    /// <param name="logger">本包的日志器。</param>
    /// <param name="options">客户端选项：超时与日志里的端点。</param>
    /// <param name="owner">客户端本身：数据变更事件的 sender。</param>
    /// <param name="nodeIds">NodeId 文本；空列表或重复项返回 <c>InvalidRequest</c>。</param>
    /// <param name="handler">数据变更处理器，在 SDK 的回调线程上逐条交付。</param>
    /// <param name="subscriptionOptions">已校验的订阅选项。</param>
    /// <param name="cancellationToken">取消令牌；只管这一次创建，不影响之后的 <see cref="OpcUaSubscription.DisposeAsync"/>。</param>
    /// <returns>订阅；逐项创建状态在 <see cref="OpcUaSubscription.ItemStatuses"/>。</returns>
    public static async Task<CommResult<OpcUaSubscription>> CreateAsync(
        OpcUaSdkSession session,
        ITelemetryContext telemetry,
        ILogger logger,
        OpcUaClientOptions options,
        object owner,
        IReadOnlyList<string> nodeIds,
        EventHandler<OpcUaDataChangedEventArgs> handler,
        OpcUaSubscriptionOptions subscriptionOptions,
        CancellationToken cancellationToken)
    {
        if (nodeIds.Count == 0)
        {
            return OpcUaSdkErrorMap.InvalidRequest("A subscription needs at least one node id.").As<OpcUaSubscription>();
        }

        if (new HashSet<string>(nodeIds, StringComparer.Ordinal).Count != nodeIds.Count)
        {
            return OpcUaSdkErrorMap
                .InvalidRequest("A subscription cannot watch the same node twice.")
                .As<OpcUaSubscription>();
        }

        if (!OpcUaSdkOperations.TryResolve(session, nodeIds, out NodeId[] resolved, out CommResult failure))
        {
            return failure.As<OpcUaSubscription>();
        }

        string[] texts = nodeIds.ToArray();
        CommResult<OpcUaSdkSubscription> created = await OpcUaSdkSubscription
            .CreateAsync(
                new OpcUaSdkSubscription.Request(session, telemetry, logger, options.OperationTimeout, owner, handler),
                resolved,
                texts,
                subscriptionOptions,
                cancellationToken)
            .ConfigureAwait(false);

        if (!created.IsSuccess)
        {
            return created.As<OpcUaSubscription>();
        }

        OpcUaSdkSubscription subscription = created.Data!;
        logger.LogDebug("Subscribed {Count} node(s) on {Endpoint}.", texts.Length, options.EndpointUrl);
        return CommResult<OpcUaSubscription>.Success(
            new OpcUaSubscription(texts, subscription.ItemStatuses, () => new ValueTask(subscription.DeleteAsync())));
    }
}
