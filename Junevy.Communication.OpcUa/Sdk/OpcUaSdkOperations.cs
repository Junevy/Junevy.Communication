using Junevy.Communication.Core.Results;
using Microsoft.Extensions.Logging;
using Opc.Ua;

namespace Junevy.Communication.OpcUa.Sdk;

/// <summary>
/// 读与写的请求组装、NodeId 解析与逐项映射（P62-D6、D7）。
/// 与 <see cref="OpcUaSdkConnection"/> 分开放是为了让连接类只管会话生命周期，本文件只管一次读写。
/// </summary>
internal static class OpcUaSdkOperations
{
    /// <summary>
    /// 读若干个节点。NodeId 先全部解析，**任何一项不合法就整批失败**，服务器收不到请求。
    /// </summary>
    /// <param name="session">已建立的会话。</param>
    /// <param name="endpointUrl">端点地址，只用于日志。</param>
    /// <param name="logger">本包的日志器。</param>
    /// <param name="nodeIds">NodeId 文本。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>与输入同序同长的逐项值。</returns>
    public static async Task<CommResult<IReadOnlyList<OpcUaValue>>> ReadAsync(
        OpcUaSdkSession session,
        string? endpointUrl,
        ILogger logger,
        IReadOnlyList<string> nodeIds,
        CancellationToken cancellationToken)
    {
        if (!TryResolve(session, nodeIds, out NodeId[] resolved, out CommResult failure))
        {
            return failure.As<IReadOnlyList<OpcUaValue>>();
        }

        try
        {
            ArrayOf<DataValue> results = await session.ReadAsync(resolved, cancellationToken).ConfigureAwait(false);
            logger.LogDebug("Read {Count} node(s) from {Endpoint}.", resolved.Length, endpointUrl);

            return CommResult<IReadOnlyList<OpcUaValue>>.Success(ToValues(results));
        }
        catch (Exception exception)
        {
            return OpcUaSdkErrorMap.Runtime(exception).As<IReadOnlyList<OpcUaValue>>();
        }
    }

    /// <summary>
    /// 写若干个节点。NodeId 与 .NET 值先全部校验，**任何一项不合法就整批失败**，服务器收不到请求。
    /// </summary>
    /// <param name="session">已建立的会话。</param>
    /// <param name="endpointUrl">端点地址，只用于日志。</param>
    /// <param name="logger">本包的日志器。</param>
    /// <param name="values">待写项。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>与输入同序同长的逐项状态码。</returns>
    public static async Task<CommResult<IReadOnlyList<OpcUaStatusCode>>> WriteAsync(
        OpcUaSdkSession session,
        string? endpointUrl,
        ILogger logger,
        IReadOnlyList<OpcUaWriteValue> values,
        CancellationToken cancellationToken)
    {
        string[] nodeIds = new string[values.Count];
        for (int i = 0; i < values.Count; i++)
        {
            nodeIds[i] = values[i].NodeId;
        }

        if (!TryResolve(session, nodeIds, out NodeId[] resolved, out CommResult failure))
        {
            return failure.As<IReadOnlyList<OpcUaStatusCode>>();
        }

        var items = new WriteValue[values.Count];
        for (int i = 0; i < values.Count; i++)
        {
            if (!OpcUaSdkValues.TryToSdk(values[i].Value, out Variant variant))
            {
                // 值映射表里没有这个 .NET 类型：写入不做类型转换，因此整批失败，不发请求。
                return OpcUaSdkErrorMap
                    .InvalidRequest(
                        $"Cannot write {values[i].Value.GetType()} to {values[i].NodeId}: the type is not supported.")
                    .As<IReadOnlyList<OpcUaStatusCode>>();
            }

            items[i] = new WriteValue
            {
                NodeId = resolved[i],
                AttributeId = Attributes.Value,
                Value = new DataValue(variant),
            };
        }

        try
        {
            ArrayOf<StatusCode> results = await session
                .WriteAsync(ArrayOf.ToArrayOf(items), cancellationToken)
                .ConfigureAwait(false);

            logger.LogDebug("Wrote {Count} node(s) on {Endpoint}.", items.Length, endpointUrl);
            return CommResult<IReadOnlyList<OpcUaStatusCode>>.Success(ToStatuses(results));
        }
        catch (Exception exception)
        {
            return OpcUaSdkErrorMap.Runtime(exception).As<IReadOnlyList<OpcUaStatusCode>>();
        }
    }

    private static bool TryResolve(
        OpcUaSdkSession session,
        IReadOnlyList<string> nodeIds,
        out NodeId[] resolved,
        out CommResult failure)
    {
        resolved = new NodeId[nodeIds.Count];
        for (int i = 0; i < nodeIds.Count; i++)
        {
            if (!OpcUaSdkNodeId.TryParse(nodeIds[i], session.NamespaceUris, out NodeId nodeId, out string? reason))
            {
                resolved = Array.Empty<NodeId>();
                failure = OpcUaSdkErrorMap.InvalidRequest($"'{nodeIds[i]}' is not a valid node id: {reason}");
                return false;
            }

            resolved[i] = nodeId;
        }

        failure = CommResult.Success();
        return true;
    }

    private static IReadOnlyList<OpcUaValue> ToValues(ArrayOf<DataValue> results)
    {
        var values = new OpcUaValue[results.Count];
        for (int i = 0; i < results.Count; i++)
        {
            values[i] = OpcUaSdkValues.FromSdk(results[i]);
        }

        return values;
    }

    private static IReadOnlyList<OpcUaStatusCode> ToStatuses(ArrayOf<StatusCode> results)
    {
        var statuses = new OpcUaStatusCode[results.Count];
        for (int i = 0; i < results.Count; i++)
        {
            statuses[i] = new OpcUaStatusCode(results[i].Code);
        }

        return statuses;
    }
}
