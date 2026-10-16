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
    /// <param name="endpointUrl">端点地址，只用于日志。</param>
    /// <param name="nodeIds">NodeId 文本；空列表或重复项返回 <c>InvalidRequest</c>。</param>
    /// <param name="handler">数据变更处理器，在 SDK 的回调线程上逐条交付。</param>
    /// <param name="options">订阅选项。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>订阅；逐项创建状态在 <see cref="OpcUaSubscription.ItemStatuses"/>。</returns>
    public static async Task<CommResult<OpcUaSubscription>> CreateAsync(
        OpcUaSdkSession session,
        ITelemetryContext telemetry,
        ILogger logger,
        string endpointUrl,
        IReadOnlyList<string> nodeIds,
        EventHandler<OpcUaDataChangedEventArgs> handler,
        OpcUaSubscriptionOptions options,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(nodeIds);
        ArgumentNullException.ThrowIfNull(handler);

        if (nodeIds.Count == 0)
        {
            return OpcUaSdkErrorMap.InvalidRequest("A subscription needs at least one node id.").As<OpcUaSubscription>();
        }

        var seen = new HashSet<string>(nodeIds, StringComparer.Ordinal);
        if (seen.Count != nodeIds.Count)
        {
            return OpcUaSdkErrorMap
                .InvalidRequest("A subscription cannot watch the same node twice.")
                .As<OpcUaSubscription>();
        }

        var resolved = new NodeId[nodeIds.Count];
        for (int i = 0; i < nodeIds.Count; i++)
        {
            if (!OpcUaSdkNodeId.TryParse(nodeIds[i], session.NamespaceUris, out NodeId nodeId, out string? reason))
            {
                return OpcUaSdkErrorMap
                    .InvalidRequest($"'{nodeIds[i]}' is not a valid node id: {reason}")
                    .As<OpcUaSubscription>();
            }

            resolved[i] = nodeId;
        }

        CommResult<OpcUaSdkSubscription> created = await OpcUaSdkSubscription
            .CreateAsync(session, telemetry, logger, resolved, nodeIds, handler, options, cancellationToken)
            .ConfigureAwait(false);

        if (!created.IsSuccess)
        {
            return created.As<OpcUaSubscription>();
        }

        OpcUaSdkSubscription subscription = created.Data!;
        logger.LogDebug("Subscribed {Count} node(s) on {Endpoint}.", nodeIds.Count, endpointUrl);
        return CommResult<OpcUaSubscription>.Success(new OpcUaSubscription(
            nodeIds.ToArray(),
            subscription.ItemStatuses,
            async () => await subscription.DeleteAsync(cancellationToken).ConfigureAwait(false)));
    }
}