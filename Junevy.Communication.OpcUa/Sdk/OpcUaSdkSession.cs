using Junevy.Communication.Core.Results;
using Junevy.Communication.Core.Utils;
using Microsoft.Extensions.Logging;
using Opc.Ua;
using Opc.Ua.Client;
using SdkSession = Opc.Ua.Client.ManagedSession;

namespace Junevy.Communication.OpcUa.Sdk;

/// <summary>
/// SDK 的 <c>ManagedSession</c> 外面的薄壳：建立、关闭、状态事件与命名空间表（P62-D2、D3、D11、D12）。
/// 重连与订阅转移交给 SDK 的 <c>ManagedSession</c>，本包不自己写重连循环（设计 14.2）。
/// </summary>
internal sealed class OpcUaSdkSession : IAsyncDisposable
{
    private readonly SdkSession session;
    private readonly ILogger logger;
    private int disposed;

    private OpcUaSdkSession(SdkSession session, ILogger logger)
    {
        this.session = session;
        this.logger = logger;
    }

    /// <summary>SDK 的连接状态事件。</summary>
    public event EventHandler<ConnectionStateChangedEventArgs>? ConnectionStateChanged;

    /// <summary>命名空间表：<c>nsu=</c> 形式的 NodeId 按它解析。</summary>
    public NamespaceTable NamespaceUris => session.NamespaceUris;

    /// <summary>服务器上报的操作上限（P62-D7）。</summary>
    public OperationLimits OperationLimits => session.OperationLimits;

    /// <summary>底层客户端：SDK 的 <c>Browser</c> 等高层助手需要它（<c>ISessionClient</c>）。</summary>
    public ISessionClient SessionClient => session;

    /// <summary>是否已连接。</summary>
    public bool Connected => session.Connected;

    /// <summary>会话标识，诊断用。</summary>
    public string SessionId => session.SessionId.IsNull ? "<none>" : session.SessionId.ToString();

    /// <summary>
    /// 建立会话。首次连接失败**不**启动后台重连（P6-C11）。
    /// </summary>
    /// <param name="options">已校验的选项。</param>
    /// <param name="endpoint">已经选好的端点（P62-D3）。</param>
    /// <param name="configuration">客户端的应用配置。</param>
    /// <param name="telemetry">遥测上下文。</param>
    /// <param name="logger">本包的日志器。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>会话；失败时给出分类与原因码。</returns>
    public static async Task<CommResult<OpcUaSdkSession>> ConnectAsync(
        OpcUaClientOptions options,
        ConfiguredEndpoint endpoint,
        ApplicationConfiguration configuration,
        ITelemetryContext telemetry,
        ILogger logger,
        CancellationToken cancellationToken)
    {
        // SDK 的 ConnectAsync 没有超时参数，不开作用域它会一直阻塞（F2）。
        using var scope = TimeoutScope.Start(options.ConnectTimeout, cancellationToken);

        try
        {
            SdkSession session = await new Opc.Ua.Client.ManagedSessionBuilder(configuration, telemetry)
                .UseEndpoint(endpoint)
                .WithUserIdentity(new UserIdentity())
                .WithSessionName(options.ApplicationName)
                .WithSessionTimeout(TimeSpan.FromMilliseconds(options.SessionTimeout))
                .WithReconnectPolicy(policy => new ReconnectPolicyOptions
                {
                    InitialDelay = TimeSpan.FromMilliseconds(options.ReconnectInterval),
                    MaxDelay = TimeSpan.FromMilliseconds(options.MaxReconnectInterval),
                    JitterFactor = 0.2,
                    MaxRetries = -1,
                    Strategy = BackoffStrategy.Exponential,
                })
                .WithTransferSubscriptionsOnRecreate(true)
                .ConnectAsync(scope.Token)
                .ConfigureAwait(false);

            session.OperationTimeout = options.OperationTimeout;
            session.KeepAliveInterval = options.KeepAliveInterval;

            var wrapped = new OpcUaSdkSession(session, logger);
            session.ConnectionStateChanged += wrapped.OnConnectionStateChanged;
            return CommResult<OpcUaSdkSession>.Success(wrapped);
        }
        catch (Exception exception)
        {
            return OpcUaSdkErrorMap.Runtime(exception, scope).As<OpcUaSdkSession>();
        }
    }

/// <summary>
    /// 读若干个节点的值。分批由 SDK 按服务器的 <c>MaxNodesPerRead</c> 自动完成，本库不写第二份。
    /// </summary>
    /// <param name="nodeIds">已解析的节点。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>与输入同序同长的逐项值。</returns>
    public async Task<ArrayOf<DataValue>> ReadAsync(IReadOnlyList<NodeId> nodeIds, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(nodeIds);

        var items = new ReadValueId[nodeIds.Count];
        for (int i = 0; i < nodeIds.Count; i++)
        {
            items[i] = new ReadValueId { NodeId = nodeIds[i], AttributeId = Attributes.Value };
        }

        ReadResponse response = await session
            .ReadAsync(null, 0, TimestampsToReturn.Both, ArrayOf.ToArrayOf(items), cancellationToken)
            .ConfigureAwait(false);

        return response.Results;
    }

    /// <summary>
    /// 写若干个节点的值。分批同样由 SDK 按 <c>MaxNodesPerWrite</c> 完成。
    /// </summary>
    /// <param name="values">待写项。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>与输入同序同长的逐项状态码。</returns>
    public async Task<ArrayOf<StatusCode>> WriteAsync(ArrayOf<WriteValue> values, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(values);

        WriteResponse response = await session
            .WriteAsync(null, values, cancellationToken)
            .ConfigureAwait(false);

        return response.Results;
    }

    /// <summary>
    /// 浏览与续取引用。继续点不在请求里，在响应里，续取用 <see cref="BrowseNextAsync"/>（F14）。
    /// </summary>
    /// <param name="requestHeader">请求头，null 表示由 SDK 填。</param>
    /// <param name="view">视图。</param>
    /// <param name="requestedMaxReferencesPerNode">每次请求的引用数上限。</param>
    /// <param name="nodesToBrowse">要浏览的节点。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>浏览响应，结果在 <c>Results</c> 里。</returns>
    public async Task<BrowseResponse> BrowseAsync(
        RequestHeader? requestHeader,
        ViewDescription view,
        uint requestedMaxReferencesPerNode,
        ArrayOf<BrowseDescription> nodesToBrowse,
        CancellationToken cancellationToken)
        => await session
            .BrowseAsync(requestHeader, view, requestedMaxReferencesPerNode, nodesToBrowse, cancellationToken)
            .ConfigureAwait(false);

    /// <summary>续取一批继续点的引用。</summary>
    /// <param name="requestHeader">请求头，null 表示由 SDK 填。</param>
    /// <param name="releaseContinuationPoints">是否在取完后释放继续点。</param>
    /// <param name="continuationPoints">继续点。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>续取响应，结果在 <c>Results</c> 里。</returns>
    public async Task<BrowseNextResponse> BrowseNextAsync(
        RequestHeader? requestHeader,
        bool releaseContinuationPoints,
        ArrayOf<ByteString> continuationPoints,
        CancellationToken cancellationToken)
        => await session
            .BrowseNextAsync(requestHeader, releaseContinuationPoints, continuationPoints, cancellationToken)
            .ConfigureAwait(false);

    /// <summary>关闭会话：先让服务器删除会话上的订阅。</summary>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>关闭结果；尽力而为，不抛异常。</returns>
    public async Task<CommResult> CloseAsync(CancellationToken cancellationToken)
    {
        try
        {
            await session.CloseAsync(cancellationToken).ConfigureAwait(false);
            return CommResult.Success();
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "Closing the OPC UA session failed; continuing with the disconnect.");
            return OpcUaSdkErrorMap.Runtime(exception);
        }
    }

    /// <summary>释放会话（可重复调用）。</summary>
    /// <returns>异步释放。</returns>
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0)
        {
            return;
        }

        session.ConnectionStateChanged -= OnConnectionStateChanged;
        await session.DisposeAsync().ConfigureAwait(false);
    }

    private void OnConnectionStateChanged(object? sender, ConnectionStateChangedEventArgs e)
        => ConnectionStateChanged?.Invoke(this, e);
}


