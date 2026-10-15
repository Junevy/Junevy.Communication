using Junevy.Communication.Core.Results;
using Junevy.Communication.Core.Utils;
using Microsoft.Extensions.Logging;
using Opc.Ua;
using Opc.Ua.Client;

namespace Junevy.Communication.OpcUa.Sdk;

/// <summary>
/// 一个客户端实例的 SDK 连接：应用配置、遥测上下文与 <c>ManagedSession</c> 都关在 SDK 边界内，
/// 外层只看得见本包自己的类型（P6-C5）。串行化与释放策略由 <see cref="OpcUaLifecycle"/> 负责，
/// 这里只做"建配置 → 选端点 → 建会话 → 收状态"这四步。
/// </summary>
internal sealed class OpcUaSdkConnection : IAsyncDisposable
{
    private readonly OpcUaClientOptions options;
    private readonly ILoggerFactory? loggerFactory;
    private readonly ILogger logger;
private readonly OpcUaSessionStateMachine stateMachine;

    /// <summary>释放与"连接被摘出去"共用：SDK 不理会取消令牌，只能靠它把后台那条连接叫停（F14）。</summary>
    private readonly CancellationTokenSource aborting = new CancellationTokenSource();

    private OpcUaSdkConfigurationSet? configuration;
    private OpcUaSdkSession? session;
    private int disposed;

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
        this.loggerFactory = loggerFactory;
        this.logger = logger;
        this.stateMachine = stateMachine;

        Telemetry = OpcUaSdkConfiguration.CreateTelemetry(loggerFactory);
    }

    /// <summary>SDK 的遥测上下文。</summary>
    public ITelemetryContext Telemetry { get; }

    /// <summary>是否已有会话。</summary>
    public bool HasSession => Volatile.Read(ref session) is not null;

    /// <summary>当前会话；没有会话时为 null。读写、浏览与订阅都经它走 SDK。</summary>
    public OpcUaSdkSession? Session => Volatile.Read(ref session);

    /// <summary>当前会话的命名空间表；没有会话时为 null。<c>nsu=</c> 形式的 NodeId 按它解析。</summary>
    public NamespaceTable? NamespaceUris => Session?.NamespaceUris;

    /// <summary>当前会话，必要时先要求已连接。</summary>
    /// <param name="owner">调用方，用于异常信息。</param>
    /// <returns>会话。</returns>
    /// <exception cref="InvalidOperationException">没有会话时。</exception>
    public OpcUaSdkSession RequireSession(object owner)
    {
        OpcUaSdkSession? current = Session;
        ObjectDisposedException.ThrowIf(IsDisposed, owner);

        return current ?? throw new InvalidOperationException(
            $"The OPC UA session is not connected; call ConnectAsync first on {owner}.");
    }

/// <summary>建立会话：建一次配置，按 P62-D3 选端点，然后建会话。</summary>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>连接结果。</returns>
    public async Task<CommResult> OpenAsync(CancellationToken cancellationToken)
    {
        // 整个连接过程共用一个作用域：端点发现也不响应超时（F2）。
        using var scope = TimeoutScope.Start(options.ConnectTimeout, cancellationToken);
        using var wait = CancellationTokenSource.CreateLinkedTokenSource(scope.Token, aborting.Token);

        // SDK 在端点发现阶段不理会取消令牌（F14），光把令牌递给它等于既没有超时也没有取消：
        // 自己先用 WaitAsync 把调用方摘出去，超时、取消与释放都立刻有结果。
        Task<CommResult> connect = ConnectCoreAsync(aborting.Token);
        _ = connect.ContinueWith(static task => _ = task.Exception, TaskContinuationOptions.OnlyOnFaulted);

        try
        {
            return await connect.WaitAsync(wait.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (aborting.IsCancellationRequested && !scope.IsTimedOut && !scope.IsUserCancelled)
        {
            return OpcUaSdkErrorMap.ConnectionClosed("The OPC UA client was disposed while connecting.");
        }
        catch (Exception exception)
        {
            return OpcUaSdkErrorMap.Runtime(exception, scope);
        }
    }

    private async Task<CommResult> ConnectCoreAsync(CancellationToken cancellationToken)
    {
        configuration ??= new OpcUaSdkConfigurationSet(
            options,
            await OpcUaSdkConfiguration.CreateAsync(options, Telemetry, cancellationToken).ConfigureAwait(false),
            Telemetry);

        CommResult<ConfiguredEndpoint> endpoint = await OpcUaSdkConfiguration
            .SelectEndpointAsync(options, configuration.Application, Telemetry, cancellationToken)
            .ConfigureAwait(false);

        if (!endpoint.IsSuccess)
        {
            logger.LogWarning("OPC UA endpoint discovery failed: {Result}", endpoint);
            return endpoint.ToResult();
        }

        CommResult<OpcUaSdkSession> opened = await OpcUaSdkSession
            .ConnectAsync(options, endpoint.Data!, configuration.Application, Telemetry, logger, cancellationToken)
            .ConfigureAwait(false);

        if (!opened.IsSuccess)
        {
            logger.LogWarning("Opening the OPC UA session failed: {Result}", opened);
            return opened.ToResult();
        }

        OpcUaSdkSession session = opened.Data!;

        // 摘出去的那段时间里客户端可能已经释放了：这时会话不能再挂上去，当场关掉。
        if (IsDisposed || aborting.IsCancellationRequested)
        {
            await session.CloseAsync(CancellationToken.None).ConfigureAwait(false);
            await session.DisposeAsync().ConfigureAwait(false);
            return OpcUaSdkErrorMap.ConnectionClosed("The OPC UA client was disposed while connecting.");
        }

        session.ConnectionStateChanged += OnSdkStateChanged;
        Volatile.Write(ref this.session, session);
        logger.LogInformation(
            "OPC UA session {SessionId} connected to {Endpoint}.",
            session.SessionId,
            options.EndpointUrl);
        return CommResult.Success();
    }

    /// <summary>叫停进行中的连接，让它立刻返回失败，不必等到 <c>ConnectTimeout</c>（F14）。</summary>
    public void AbortConnect()
    {
        if (!aborting.IsCancellationRequested)
        {
            aborting.Cancel();
        }
    }

    /// <summary>关闭并释放会话（可重复调用）。关闭失败尽力而为，返回失败结果。</summary>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>关闭结果；没有会话时直接成功。</returns>
    public async Task<CommResult> CloseAsync(CancellationToken cancellationToken)
    {
        OpcUaSdkSession? current = Interlocked.Exchange(ref session, null);
        if (current is null)
        {
            return CommResult.Success();
        }

        current.ConnectionStateChanged -= OnSdkStateChanged;

        try
        {
            return await current.CloseAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            await current.DisposeAsync().ConfigureAwait(false);
        }
    }

    /// <summary>是否已释放。</summary>
    public bool IsDisposed => Volatile.Read(ref disposed) != 0;

    /// <summary>释放连接：先关会话再释放自身（可重复调用）。</summary>
    /// <returns>异步释放。</returns>
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0)
        {
            return;
        }

await CloseAsync(CancellationToken.None).ConfigureAwait(false);
        aborting.Cancel();
        aborting.Dispose();
    }

    // SDK 的状态事件每次重试都会触发（F17）；映射与去重都在状态机里做。
    private void OnSdkStateChanged(object? sender, ConnectionStateChangedEventArgs e)
        => stateMachine.MoveTo(Map(e.NewState), e.Error?.ToString());

    private static OpcUaSessionState Map(ConnectionState state) => state switch
    {
        ConnectionState.Connecting => OpcUaSessionState.Connecting,
        ConnectionState.Connected => OpcUaSessionState.Connected,
        ConnectionState.Reconnecting or ConnectionState.Failover => OpcUaSessionState.Reconnecting,
        _ => OpcUaSessionState.Disconnected,
    };
}


