using System.Diagnostics;
using Junevy.Communication.Core.Results;
using Junevy.Communication.Core.Utils;
using Microsoft.Extensions.Logging;
using Opc.Ua;
using Opc.Ua.Client;

namespace Junevy.Communication.OpcUa.Sdk;

/// <summary>
/// 读、写与浏览的请求组装、NodeId 解析与逐项映射（P62-D6、D7、D9）。
/// 与 <see cref="OpcUaSdkConnection"/> 分开放是为了让连接类只管会话生命周期，本文件只管一次服务调用。
/// 每次调用都包在一个 <c>OperationTimeout</c> 作用域里（P62-D12）：超时与调用方取消以作用域为准，
/// 不看 SDK 抛出的异常类型——SDK 会把取消包成带 Bad 码的服务异常（F31）。
/// </summary>
internal static class OpcUaSdkOperations
{
    // 每次 Browse 请求要的引用数。传 0 表示"用服务器默认值"，那样不会产生继续点，
    // 服务器的操作上限也不生效（F13），所以这里给一个自己的上限并把继续点追完。
    private const uint MaxReferencesPerRequest = 1000;

    // 单次 BrowseAsync 调用返回的引用数上限：够大就不会被迫多次调用，同时给失控的节点树兜个底。
    private const uint MaxReferencesPerBrowseCall = 100000;

    // 0xFFFFFFFF：给 int.MaxValue 时服务器回上来的节点类别全是 Unspecified（F26）。
    private const int AllNodeClasses = -1;

    private static ViewDescription AllView { get; } = new ViewDescription
    {
        ViewId = NodeId.Null,
        Timestamp = DateTimeUtc.MinValue,
        ViewVersion = 0,
    };

    /// <summary>
    /// 读若干个节点。NodeId 先全部解析，**任何一项不合法就整批失败**，服务器收不到请求。
    /// </summary>
    /// <param name="session">已建立的会话。</param>
    /// <param name="options">客户端选项：超时与日志里的端点。</param>
    /// <param name="logger">本包的日志器。</param>
    /// <param name="nodeIds">NodeId 文本。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>与输入同序同长的逐项值。</returns>
    public static async Task<CommResult<IReadOnlyList<OpcUaValue>>> ReadAsync(
        OpcUaSdkSession session,
        OpcUaClientOptions options,
        ILogger logger,
        IReadOnlyList<string> nodeIds,
        CancellationToken cancellationToken)
    {
        if (!TryResolve(session, nodeIds, out NodeId[] resolved, out CommResult failure))
        {
            return failure.As<IReadOnlyList<OpcUaValue>>();
        }

        return await CallAsync(
                "Read",
                resolved.Length,
                options,
                logger,
                async token => ToValues(await session.ReadAsync(resolved, token).ConfigureAwait(false)),
                cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// 写若干个节点。NodeId 与 .NET 值先全部校验，**任何一项不合法就整批失败**，服务器收不到请求。
    /// </summary>
    /// <param name="session">已建立的会话。</param>
    /// <param name="options">客户端选项：超时与日志里的端点。</param>
    /// <param name="logger">本包的日志器。</param>
    /// <param name="values">待写项。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>与输入同序同长的逐项状态码。</returns>
    public static async Task<CommResult<IReadOnlyList<OpcUaStatusCode>>> WriteAsync(
        OpcUaSdkSession session,
        OpcUaClientOptions options,
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

        return await CallAsync(
                "Write",
                items.Length,
                options,
                logger,
                async token => ToStatuses(await session.WriteAsync(ArrayOf.ToArrayOf(items), token).ConfigureAwait(false)),
                cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// 浏览一个节点的正向层级引用，继续点一直追完（P62-D9）。
    /// </summary>
    /// <param name="session">已建立的会话。</param>
    /// <param name="options">客户端选项：超时与日志里的端点。</param>
    /// <param name="logger">本包的日志器。</param>
    /// <param name="nodeId">起点 NodeId 文本。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>完整的引用列表。</returns>
    public static async Task<CommResult<IReadOnlyList<OpcUaReference>>> BrowseAsync(
        OpcUaSdkSession session,
        OpcUaClientOptions options,
        ILogger logger,
        string nodeId,
        CancellationToken cancellationToken)
    {
        if (!TryResolve(session, new[] { nodeId }, out NodeId[] start, out CommResult failure))
        {
            return failure.As<IReadOnlyList<OpcUaReference>>();
        }

        return await CallAsync("Browse", 1, options, logger, token => BrowseCoreAsync(session, start[0], token), cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// 把一批 NodeId 文本全部解析好；任何一项不合法就给出 <see cref="CommErrorKind.InvalidRequest"/>，写明是哪一项。
    /// </summary>
    /// <param name="session">提供命名空间表（<c>nsu=</c> 形式要用）。</param>
    /// <param name="nodeIds">NodeId 文本。</param>
    /// <param name="resolved">解析结果，与输入同序；失败时为空数组。</param>
    /// <param name="failure">失败结果；成功时为成功结果。</param>
    /// <returns>全部合法时为 true；会话刚被并发的断开关掉时为 false，失败结果是 <see cref="CommErrorKind.NotConnected"/>。</returns>
    public static bool TryResolve(
        OpcUaSdkSession session,
        IReadOnlyList<string> nodeIds,
        out NodeId[] resolved,
        out CommResult failure)
    {
        resolved = Array.Empty<NodeId>();

        NamespaceTable namespaces;
        try
        {
            namespaces = session.NamespaceUris;
        }
        catch (ServiceResultException)
        {
            // 会话已经关闭时 SDK 连命名空间表都不给，直接抛"未连接"（F31）。
            failure = OpcUaSdkErrorMap.NotConnected("The OPC UA session was closed while the call started.");
            return false;
        }

        var parsed = new NodeId[nodeIds.Count];
        for (int i = 0; i < nodeIds.Count; i++)
        {
            if (!OpcUaSdkNodeId.TryParse(nodeIds[i], namespaces, out NodeId nodeId, out string? reason))
            {
                failure = OpcUaSdkErrorMap.InvalidRequest($"'{nodeIds[i]}' is not a valid node id: {reason}");
                return false;
            }

            parsed[i] = nodeId;
        }

        resolved = parsed;
        failure = CommResult.Success();
        return true;
    }

    // 一次服务调用：OperationTimeout 作用域、失败分类与日志（节点数、耗时，不记录值；P62-D13）都在这里，读、写、浏览共用。
    private static async Task<CommResult<T>> CallAsync<T>(
        string operation,
        int nodeCount,
        OpcUaClientOptions options,
        ILogger logger,
        Func<CancellationToken, Task<T>> call,
        CancellationToken cancellationToken)
    {
        using var scope = TimeoutScope.Start(options.OperationTimeout, cancellationToken);
        Stopwatch timer = Stopwatch.StartNew();

        CommResult<T> outcome;
        try
        {
            outcome = CommResult<T>.Success(await call(scope.Token).ConfigureAwait(false));
        }
        catch (Exception exception)
        {
            outcome = OpcUaSdkErrorMap.Runtime(exception, scope).As<T>();
        }

        if (outcome.IsSuccess)
        {
            logger.LogDebug(
                "OPC UA {Operation} of {Count} node(s) on {Endpoint} completed in {Elapsed} ms.",
                operation,
                nodeCount,
                options.EndpointUrl,
                timer.ElapsedMilliseconds);
        }
        else
        {
            logger.LogWarning(
                "OPC UA {Operation} of {Count} node(s) on {Endpoint} failed after {Elapsed} ms: {Outcome}.",
                operation,
                nodeCount,
                options.EndpointUrl,
                timer.ElapsedMilliseconds,
                outcome);
        }

        return outcome;
    }

    private static async Task<IReadOnlyList<OpcUaReference>> BrowseCoreAsync(
        OpcUaSdkSession session,
        NodeId start,
        CancellationToken cancellationToken)
    {
        // 继续点由 SDK 的 Browser 自己追完：手写 BrowseNext 在 2.0 会被判成 BadContinuationPointInvalid（F25）。
        var browser = new Browser(session.SessionClient, new BrowserOptions
        {
            View = AllView,
            BrowseDirection = BrowseDirection.Forward,
            ReferenceTypeId = ReferenceTypeIds.HierarchicalReferences,
            IncludeSubtypes = true,
            NodeClassMask = AllNodeClasses,
            ResultMask = (uint)Opc.Ua.BrowseResultMask.All,
            MaxNodesPerBrowse = MaxReferencesPerRequest,
            MaxBrowseContinuationPoints = 1,
            MaxReferencesReturned = MaxReferencesPerBrowseCall,
        });

        ArrayOf<ReferenceDescription> found = await browser.BrowseAsync(start, cancellationToken).ConfigureAwait(false);

        var references = new List<OpcUaReference>(found.Count);
        foreach (ReferenceDescription description in found)
        {
            if (!description.NodeId.IsNull)
            {
                references.Add(ToReference(description));
            }
        }

        return references;
    }

    private static OpcUaReference ToReference(ReferenceDescription description) => new OpcUaReference(
        description.NodeId.ToString(),
        description.BrowseName.ToString(),
        description.DisplayName.Text ?? string.Empty,
        (OpcUaNodeClass)(int)description.NodeClass);

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
