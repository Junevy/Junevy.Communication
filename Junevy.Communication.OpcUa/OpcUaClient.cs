using Junevy.Communication.Core.Results;
using Junevy.Communication.OpcUa.Sdk;
using Microsoft.Extensions.Logging;

namespace Junevy.Communication.OpcUa;

/// <summary>
/// OPC UA 客户端。只支持 <c>None</c> 安全策略与匿名登录（P62-D3），只做客户端。
/// 会话交给 SDK 的 <c>ManagedSession</c>：重连与订阅转移由 SDK 完成（P62-D2、D11）。
/// </summary>
public sealed class OpcUaClient : IDisposable, IAsyncDisposable
{
    private readonly OpcUaClientOptions options;
    private readonly OpcUaSessionStateMachine stateMachine;
    private readonly OpcUaLifecycle lifecycle;

    private OpcUaClient(OpcUaClientOptions options, ILoggerFactory? loggerFactory)
    {
        this.options = options;
        logger = CreateLogger(loggerFactory);
        stateMachine = new OpcUaSessionStateMachine(this, logger);
        lifecycle = new OpcUaLifecycle(this, options, loggerFactory, logger, stateMachine);
    }

    private readonly ILogger logger;

    /// <summary>
    /// 创建客户端。选项在这里校验并**取快照**，之后调用方再改选项不影响已创建的客户端（P6-C9）。
    /// </summary>
    /// <param name="options">选项；校验失败时抛 <see cref="ArgumentException"/> 族。</param>
    /// <param name="loggerFactory">日志工厂；为 null 时不记录日志。SDK 自身的日志也交给它（P62-D13）。</param>
    /// <returns>客户端；此时还没有连接。</returns>
    /// <exception cref="ArgumentNullException"><paramref name="options"/> 为 null。</exception>
    /// <exception cref="ArgumentException">某项选项不合法。</exception>
    public static OpcUaClient Create(OpcUaClientOptions options, ILoggerFactory? loggerFactory = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        OpcUaOptionsValidator.Validate(options);
        return new OpcUaClient(Snapshot(options), loggerFactory);
    }

    /// <summary>当前会话状态。</summary>
    public OpcUaSessionState State => stateMachine.State;

    /// <summary>状态变化事件；只在状态真的改变时按顺序发出。</summary>
    public event EventHandler<OpcUaSessionStateChangedEventArgs>? StateChanged
    {
        add => stateMachine.StateChanged += value;
        remove => stateMachine.StateChanged -= value;
    }

    /// <summary>
    /// 连接：发现端点、选中 <c>None</c> + 匿名的端点并建立会话。
    /// 已是 <see cref="OpcUaSessionState.Connected"/> 时直接成功；连接进行中返回
    /// <see cref="CommErrorKind.InvalidRequest"/>。首次连接失败不启动后台重连（P6-C11）。
    /// </summary>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>连接结果；服务器没有 None 端点或不允许匿名时为 <see cref="CommErrorKind.NotSupported"/>。</returns>
    /// <exception cref="ObjectDisposedException">客户端已释放。</exception>
    public Task<CommResult> ConnectAsync(CancellationToken cancellationToken = default)
        => lifecycle.ConnectAsync(cancellationToken);

    /// <summary>
    /// 断开：关闭会话并回到 <see cref="OpcUaSessionState.Disconnected"/>。可以重复调用。
    /// </summary>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>断开结果；已释放时为 <see cref="CommErrorKind.ConnectionClosed"/>。</returns>
    public Task<CommResult> DisconnectAsync(CancellationToken cancellationToken = default)
        => lifecycle.DisconnectAsync(cancellationToken);

    /// <summary>
    /// 读一个或多个节点（P62-D7）。
    /// </summary>
    /// <param name="nodeIds">NodeId 文本；空列表直接成功，不发请求。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>与输入同序同长的逐项结果。</returns>
    public async Task<CommResult<IReadOnlyList<OpcUaValue>>> ReadAsync(
        IReadOnlyList<string> nodeIds,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(nodeIds);

        if (!TryBeginOperation(out CommResult failure))
        {
            return failure.As<IReadOnlyList<OpcUaValue>>();
        }

        return await lifecycle.Connection.ReadAsync(this, nodeIds, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// 写一个或多个节点（P62-D6、D7）。
    /// </summary>
    /// <param name="values">待写项；空列表直接成功，不发请求。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>与输入同序同长的逐项状态码。</returns>
    public async Task<CommResult<IReadOnlyList<OpcUaStatusCode>>> WriteAsync(
        IReadOnlyList<OpcUaWriteValue> values,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(values);

        if (!TryBeginOperation(out CommResult failure))
        {
            return failure.As<IReadOnlyList<OpcUaStatusCode>>();
        }

        return await lifecycle.Connection.WriteAsync(this, values, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// 浏览一个节点的正向层级引用（P62-D9）；继续点由本库处理完。
    /// </summary>
    /// <param name="nodeId">起点 NodeId 文本。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>完整的引用列表。</returns>
    public async Task<CommResult<IReadOnlyList<OpcUaReference>>> BrowseAsync(
        string nodeId,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(nodeId);

        if (!TryBeginOperation(out CommResult failure))
        {
            return failure.As<IReadOnlyList<OpcUaReference>>();
        }

        return await lifecycle.Connection.BrowseAsync(this, nodeId, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// 订阅一个或多个节点的值变化（P62-D10）。
    /// </summary>
    /// <param name="nodeIds">NodeId 文本；空列表或重复返回 <see cref="CommErrorKind.InvalidRequest"/>。</param>
    /// <param name="handler">通知处理器，在 SDK 的回调线程上逐条交付。</param>
    /// <param name="options">订阅选项；为 null 时用默认值。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>订阅；逐项创建状态在 <see cref="OpcUaSubscription.ItemStatuses"/>。</returns>
    public Task<CommResult<OpcUaSubscription>> SubscribeAsync(
        IReadOnlyList<string> nodeIds,
        EventHandler<OpcUaDataChangedEventArgs> handler,
        OpcUaSubscriptionOptions? options = null,
        CancellationToken cancellationToken = default)
        => throw new NotSupportedException("Implemented in Task 6.");

    /// <summary>
    /// 释放（同步）。等价于 <see cref="DisposeAsync"/> 的阻塞版本；两者都可以重复调用。
    /// </summary>
    public void Dispose() => DisposeAsync().AsTask().GetAwaiter().GetResult();

    /// <summary>
    /// 释放：取消进行中的连接、关闭会话、释放 SDK 资源。之后 <see cref="ConnectAsync"/> 抛
    /// <see cref="ObjectDisposedException"/>，其余操作返回 <see cref="CommErrorKind.ConnectionClosed"/>（P6-C8）。
    /// </summary>
    /// <returns>异步释放。</returns>
    public ValueTask DisposeAsync() => lifecycle.DisposeAsync();

    /// <summary>可读文本，用于日志与诊断。</summary>
    /// <returns>客户端与当前状态。</returns>
    public override string ToString() => $"OpcUaClient({options.EndpointUrl}, {State})";

    // 释放后是 ConnectionClosed，没会话是 NotConnected（P62-D2 的四个操作共用这条规则）。
    private bool TryBeginOperation(out CommResult failure)
    {
        if (lifecycle.IsDisposed)
        {
            failure = OpcUaSdkErrorMap.ConnectionClosed("The OPC UA client is disposed.");
            return false;
        }

        if (!lifecycle.Connection.HasSession)
        {
            failure = OpcUaSdkErrorMap.NotConnected("The OPC UA client is not connected.");
            return false;
        }

        failure = CommResult.Success();
        return true;
    }

    // 取快照：Create 之后调用方再改自己的 options 对象不应该影响这个客户端（P6-C9）。
    private static OpcUaClientOptions Snapshot(OpcUaClientOptions source) => new OpcUaClientOptions
    {
        EndpointUrl = source.EndpointUrl,
        ApplicationName = source.ApplicationName,
        ApplicationUri = source.ApplicationUri,
        PkiRoot = source.PkiRoot,
        ConnectTimeout = source.ConnectTimeout,
        OperationTimeout = source.OperationTimeout,
        SessionTimeout = source.SessionTimeout,
        KeepAliveInterval = source.KeepAliveInterval,
        ReconnectInterval = source.ReconnectInterval,
        MaxReconnectInterval = source.MaxReconnectInterval,
    };

    private static ILogger CreateLogger(ILoggerFactory? loggerFactory)
        => loggerFactory?.CreateLogger("Junevy.Communication.OpcUa")
           ?? Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance;
}