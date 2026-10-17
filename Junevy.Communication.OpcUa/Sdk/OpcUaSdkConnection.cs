using Junevy.Communication.Core.Results;
using Junevy.Communication.Core.Utils;
using Microsoft.Extensions.Logging;
using Opc.Ua;
using Opc.Ua.Client;

namespace Junevy.Communication.OpcUa.Sdk;

/// <summary>
/// 一个客户端实例的 SDK 连接：应用配置、遥测上下文与 <c>ManagedSession</c> 都关在 SDK 边界内，
/// 外层只看得见本包自己的类型（P6-C5）。串行化与释放策略由 <see cref="OpcUaLifecycle"/> 负责，
/// 这里只做"建配置 → 选端点 → 建会话 → 收状态"这四步，以及把读写、浏览、订阅交给当前会话。
/// </summary>
internal sealed class OpcUaSdkConnection
{
    private readonly OpcUaClientOptions options;
    private readonly ILogger logger;
    private readonly OpcUaSessionStateMachine stateMachine;

    /// <summary>
    /// 释放时叫停进行中的连接：SDK 在端点发现阶段不理会取消令牌（F23），只能靠它把调用方摘出去。
    /// 不释放这个令牌源：被摘出去的那条后台连接可能还拿着它的令牌（P6.1 验收发现 R3）。
    /// </summary>
    private readonly CancellationTokenSource aborting = new CancellationTokenSource();

    private ApplicationConfiguration? application;
    private OpcUaSdkSession? session;

    /// <summary>
    /// 构造连接。
    /// </summary>
    /// <param name="options">已校验的选项。</param>
    /// <param name="loggerFactory">日志工厂，交给 SDK 的遥测上下文。</param>
    /// <param name="logger">本包的日志器。</param>
    /// <param name="stateMachine">接收 SDK 状态事件的状态机。</param>
    public OpcUaSdkConnection(
        OpcUaClientOptions options,
        ILoggerFactory? loggerFactory,
        ILogger logger,
        OpcUaSessionStateMachine stateMachine)
    {
        this.options = options;
        this.logger = logger;
        this.stateMachine = stateMachine;

        Telemetry = OpcUaSdkConfiguration.CreateTelemetry(loggerFactory);
    }

    /// <summary>SDK 的遥测上下文。</summary>
    public ITelemetryContext Telemetry { get; }

    /// <summary>是否已有会话。</summary>
    public bool HasSession => Volatile.Read(ref session) is not null;

    /// <summary>
    /// 建立会话：建一次配置，按 P62-D3 选端点，然后建会话。会话只在这里（调用方仍在等待时）挂上；
    /// 调用方被摘出去之后才连上的会话当场关掉（<see cref="CloseIfLate"/>）。
    /// </summary>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>连接结果。</returns>
    public async Task<CommResult> OpenAsync(CancellationToken cancellationToken)
    {
        // 整个连接过程共用一个作用域：SDK 的 ConnectAsync 没有超时参数（F2）。
        using var scope = TimeoutScope.Start(options.ConnectTimeout, cancellationToken);
        using var wait = CancellationTokenSource.CreateLinkedTokenSource(scope.Token, aborting.Token);

        // SDK 在端点发现阶段不理会取消令牌（F23），光把令牌递给它等于既没有超时也没有取消：
        // 自己先用 WaitAsync 把调用方摘出去，超时、取消与释放都立刻有结果。
        Task<CommResult<OpcUaSdkSession>> connect = ConnectCoreAsync(aborting.Token);
        _ = connect.ContinueWith(
            static task => _ = task.Exception,
            CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);

        try
        {
            CommResult<OpcUaSdkSession> opened = await connect.WaitAsync(wait.Token).ConfigureAwait(false);
            if (!opened.IsSuccess)
            {
                return opened.ToResult();
            }

            Attach(opened.Data!);
            return CommResult.Success();
        }
        catch (OperationCanceledException) when (aborting.IsCancellationRequested && !scope.IsTimedOut && !scope.IsUserCancelled)
        {
            CloseIfLate(connect);
            return OpcUaSdkErrorMap.ConnectionClosed("The OPC UA client was disposed while connecting.");
        }
        catch (Exception exception)
        {
            CloseIfLate(connect);
            return OpcUaSdkErrorMap.Runtime(exception, scope);
        }
    }

    /// <summary>
    /// 读若干个节点。NodeId 与逐项映射交给 <see cref="OpcUaSdkOperations"/>；空列表直接成功。
    /// </summary>
    /// <param name="nodeIds">NodeId 文本。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>与输入同序同长的逐项值；会话刚被断开时为 <see cref="CommErrorKind.NotConnected"/>。</returns>
    public Task<CommResult<IReadOnlyList<OpcUaValue>>> ReadAsync(
        IReadOnlyList<string> nodeIds,
        CancellationToken cancellationToken)
    {
        if (nodeIds.Count == 0)
        {
            return Task.FromResult(CommResult<IReadOnlyList<OpcUaValue>>.Success(Array.Empty<OpcUaValue>()));
        }

        OpcUaSdkSession? current = Volatile.Read(ref session);
        return current is null
            ? Gone<IReadOnlyList<OpcUaValue>>()
            : OpcUaSdkOperations.ReadAsync(current, options, logger, nodeIds, cancellationToken);
    }

    /// <summary>
    /// 写若干个节点。NodeId、值与逐项映射交给 <see cref="OpcUaSdkOperations"/>；空列表直接成功。
    /// </summary>
    /// <param name="values">待写项。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>与输入同序同长的逐项状态码；会话刚被断开时为 <see cref="CommErrorKind.NotConnected"/>。</returns>
    public Task<CommResult<IReadOnlyList<OpcUaStatusCode>>> WriteAsync(
        IReadOnlyList<OpcUaWriteValue> values,
        CancellationToken cancellationToken)
    {
        if (values.Count == 0)
        {
            return Task.FromResult(CommResult<IReadOnlyList<OpcUaStatusCode>>.Success(Array.Empty<OpcUaStatusCode>()));
        }

        OpcUaSdkSession? current = Volatile.Read(ref session);
        return current is null
            ? Gone<IReadOnlyList<OpcUaStatusCode>>()
            : OpcUaSdkOperations.WriteAsync(current, options, logger, values, cancellationToken);
    }

    /// <summary>
    /// 浏览一个节点的正向层级引用。继续点由 <see cref="OpcUaSdkOperations"/> 追完。
    /// </summary>
    /// <param name="nodeId">起点 NodeId 文本。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>完整的引用列表；会话刚被断开时为 <see cref="CommErrorKind.NotConnected"/>。</returns>
    public Task<CommResult<IReadOnlyList<OpcUaReference>>> BrowseAsync(
        string nodeId,
        CancellationToken cancellationToken)
    {
        OpcUaSdkSession? current = Volatile.Read(ref session);
        return current is null
            ? Gone<IReadOnlyList<OpcUaReference>>()
            : OpcUaSdkOperations.BrowseAsync(current, options, logger, nodeId, cancellationToken);
    }

    /// <summary>
    /// 订阅若干个节点的值变化（P62-D10）。NodeId 先全部解析，任何一项不合法就整批失败。
    /// </summary>
    /// <param name="owner">客户端本身：数据变更事件的 sender。</param>
    /// <param name="nodeIds">NodeId 文本。</param>
    /// <param name="handler">数据变更处理器，在 SDK 的回调线程上逐条交付。</param>
    /// <param name="subscriptionOptions">已校验的订阅选项。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>订阅；会话刚被断开时为 <see cref="CommErrorKind.NotConnected"/>。</returns>
    public Task<CommResult<OpcUaSubscription>> SubscribeAsync(
        object owner,
        IReadOnlyList<string> nodeIds,
        EventHandler<OpcUaDataChangedEventArgs> handler,
        OpcUaSubscriptionOptions subscriptionOptions,
        CancellationToken cancellationToken)
    {
        OpcUaSdkSession? current = Volatile.Read(ref session);
        return current is null
            ? Gone<OpcUaSubscription>()
            : OpcUaSdkSubscribeRequest.CreateAsync(
                current,
                Telemetry,
                logger,
                options,
                owner,
                nodeIds,
                handler,
                subscriptionOptions,
                cancellationToken);
    }

    /// <summary>叫停进行中的连接，让它立刻返回失败，不必等到 <c>ConnectTimeout</c>（F23）。</summary>
    public void AbortConnect() => aborting.Cancel();

    /// <summary>
    /// 摘下当前会话。摘下是同步的：此后这个会话晚到的状态事件不再改动状态机，
    /// 调用方可以先摘下、再改状态、最后用 <see cref="CloseAsync"/> 慢慢关。
    /// </summary>
    /// <returns>摘下的会话；本来就没有会话时为 null。</returns>
    public OpcUaSdkSession? Detach() => Interlocked.Exchange(ref session, null);

    /// <summary>关闭并释放一个已摘下的会话。关闭尽力而为（最多一个 <c>OperationTimeout</c>），失败只给结果、不抛异常。</summary>
    /// <param name="detached"><see cref="Detach"/> 的返回值；为 null 时直接成功。</param>
    /// <param name="cancellationToken">取消令牌；已取消时跳过与服务器的告别，直接释放。</param>
    /// <returns>关闭结果。</returns>
    public Task<CommResult> CloseAsync(OpcUaSdkSession? detached, CancellationToken cancellationToken)
        => detached is null
            ? Task.FromResult(CommResult.Success())
            : CloseDetachedAsync(detached, cancellationToken);

    private async Task<CommResult<OpcUaSdkSession>> ConnectCoreAsync(CancellationToken cancellationToken)
    {
        application ??= await OpcUaSdkConfiguration.CreateAsync(options, Telemetry, cancellationToken).ConfigureAwait(false);

        CommResult<ConfiguredEndpoint> endpoint = await OpcUaSdkConfiguration
            .SelectEndpointAsync(options, application, Telemetry, cancellationToken)
            .ConfigureAwait(false);

        if (!endpoint.IsSuccess)
        {
            logger.LogWarning("OPC UA endpoint discovery failed: {Result}", endpoint);
            return endpoint.As<OpcUaSdkSession>();
        }

        CommResult<OpcUaSdkSession> opened = await OpcUaSdkSession
            .ConnectAsync(options, endpoint.Data!, application, Telemetry, logger, cancellationToken)
            .ConfigureAwait(false);

        if (!opened.IsSuccess)
        {
            logger.LogWarning("Opening the OPC UA session failed: {Result}", opened);
        }

        return opened;
    }

    private void Attach(OpcUaSdkSession opened)
    {
        opened.ConnectionStateChanged += OnSdkStateChanged;
        Volatile.Write(ref session, opened);
        logger.LogInformation("OPC UA session {SessionId} connected to {Endpoint}.", opened.SessionId, options.EndpointUrl);
    }

    // 调用方已经被摘出去（超时、取消或释放），SDK 那条后台连接却还在跑：它要是之后才连上，
    // 那个会话不能挂到客户端上，也不能留在服务器上，当场关掉。失败的那种由 OpenAsync 里的续体观察掉（Q40）。
    private void CloseIfLate(Task<CommResult<OpcUaSdkSession>> connect)
        => _ = connect.ContinueWith(
            task => task.Result.IsSuccess
                ? CloseDetachedAsync(task.Result.Data!, CancellationToken.None)
                : Task.FromResult(CommResult.Success()),
            CancellationToken.None,
            TaskContinuationOptions.OnlyOnRanToCompletion,
            TaskScheduler.Default).Unwrap();

    private async Task<CommResult> CloseDetachedAsync(OpcUaSdkSession detached, CancellationToken cancellationToken)
    {
        detached.ConnectionStateChanged -= OnSdkStateChanged;
        using var scope = TimeoutScope.Start(options.OperationTimeout, cancellationToken);

        try
        {
            return await detached.CloseAsync(scope.Token).ConfigureAwait(false);
        }
        finally
        {
            await detached.DisposeAsync().ConfigureAwait(false);
        }
    }

    // SDK 的状态事件每次重试都会触发（F17），去重在状态机里做。已经摘下的会话（断开、释放）晚到的事件直接丢掉，
    // 否则断开之后状态可能又被改回 Reconnecting 或 Connected，而客户端手里已经没有会话了。
    private void OnSdkStateChanged(object? sender, ConnectionStateChangedEventArgs e)
    {
        if (ReferenceEquals(sender, Volatile.Read(ref session)))
        {
            stateMachine.MoveTo(Map(e.NewState), e.Error?.ToString());
        }
    }

    // 状态检查与取会话之间，会话可能刚被并发的断开摘下：给 NotConnected，而不是抛异常（P6-C8）。
    private static Task<CommResult<T>> Gone<T>()
        => Task.FromResult(OpcUaSdkErrorMap.NotConnected("The OPC UA session was closed while the call started.").As<T>());

    private static OpcUaSessionState Map(ConnectionState state) => state switch
    {
        ConnectionState.Connecting => OpcUaSessionState.Connecting,
        ConnectionState.Connected => OpcUaSessionState.Connected,
        ConnectionState.Reconnecting or ConnectionState.Failover => OpcUaSessionState.Reconnecting,
        _ => OpcUaSessionState.Disconnected,
    };
}
