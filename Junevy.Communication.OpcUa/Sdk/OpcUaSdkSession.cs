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

    /// <summary>底层客户端：SDK 的 <c>Browser</c> 等高层助手需要它（<c>ISessionClient</c>）。</summary>
    public ISessionClient SessionClient => session;

    /// <summary>会话已连接且不在重连中：此时发出的请求会立刻得到服务器的回应。</summary>
    public bool IsUsable => session.Connected && !session.Reconnecting;

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

        var reconnectPolicy = new OpcUaSdkReconnectPolicy(options);

        try
        {
            SdkSession session = await new Opc.Ua.Client.ManagedSessionBuilder(configuration, telemetry)
                .UseEndpoint(endpoint)
                .WithUserIdentity(OpcUaSdkIdentity.Create(options))
                .WithSessionName(options.ApplicationName)
                .WithSessionTimeout(TimeSpan.FromMilliseconds(options.SessionTimeout))
                .WithReconnectPolicy(reconnectPolicy)
                .WithTransferSubscriptionsOnRecreate(true)
                .ConnectAsync(scope.Token)
                .ConfigureAwait(false);

            reconnectPolicy.MarkEstablished();

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

    /// <summary>把 SDK 的订阅挂到会话上（重连时由 SDK 自己转移或重建）。</summary>
    /// <param name="subscription">SDK 的订阅。</param>
    public void AddSubscription(Subscription subscription) => session.AddSubscription(subscription);

    /// <summary>
    /// 把订阅从会话上摘下并删除服务器上的订阅。只调 <c>Subscription.DeleteAsync</c> 不够：
    /// 订阅还挂在会话上，会话恢复时 SDK 会把它在服务器上重建出来（F30）。
    /// </summary>
    /// <param name="subscription">SDK 的订阅。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>异步操作。</returns>
    public Task RemoveSubscriptionAsync(Subscription subscription, CancellationToken cancellationToken)
        => session.RemoveSubscriptionAsync(subscription, cancellationToken);

    /// <summary>
    /// 等到 SDK 报告 <c>Connected</c>。重连时 SDK 先重新激活会话、再转移或重建订阅，最后才报 <c>Connected</c>，
    /// 所以等到它之后再发的请求看到的是恢复完成的会话（F32）。
    /// </summary>
    /// <returns>会话已连接时立刻完成；会话关闭时以 <see cref="ObjectDisposedException"/> 失败。</returns>
    public async Task WaitUntilConnectedAsync()
    {
        var recovered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        void OnChanged(object? sender, ConnectionStateChangedEventArgs e)
        {
            if (e.NewState == ConnectionState.Connected)
            {
                recovered.TrySetResult();
            }
            else if (e.NewState is ConnectionState.Closing or ConnectionState.Closed)
            {
                recovered.TrySetException(new ObjectDisposedException(nameof(OpcUaSdkSession), "The OPC UA session was closed."));
            }
        }

        // 先挂事件再看当前状态：否则两步之间恢复完成的那次事件会被漏掉。
        session.ConnectionStateChanged += OnChanged;
        try
        {
            if (IsUsable)
            {
                return;
            }

            await recovered.Task.ConfigureAwait(false);
        }
        finally
        {
            session.ConnectionStateChanged -= OnChanged;
        }
    }

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

        try
        {
            await session.DisposeAsync().ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            // 释放不抛异常（P6-C8）：它也在"被摘出去的连接"的后台续体里调用，异常没有人观察。
            logger.LogWarning(exception, "Disposing the OPC UA session failed; the session is dropped anyway.");
        }
    }

    private void OnConnectionStateChanged(object? sender, ConnectionStateChangedEventArgs e)
        => ConnectionStateChanged?.Invoke(this, e);
}
