using System.Diagnostics;
using Junevy.Communication.Core.Diagnostics;
using Junevy.Communication.Core.Results;
using Junevy.Communication.Mqtt.Sdk;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Junevy.Communication.Mqtt;

/// <summary>
/// MQTT 客户端。连接、发布、订阅、接收、重连与恢复订阅；只做客户端。
/// </summary>
/// <remarks>
/// <para>错误一律走结果（<see cref="CommResult"/>），SDK 抛出的异常不会逃出公开方法。参数为 null 抛
/// <see cref="ArgumentNullException"/>；释放之后 <see cref="ConnectAsync"/> 抛 <see cref="ObjectDisposedException"/>，
/// 其余操作返回 <see cref="CommErrorKind.ConnectionClosed"/>。<see cref="Dispose"/> 与 <see cref="DisposeAsync"/>
/// 可以重复调用。</para>
/// <para><see cref="DisconnectAsync"/> 没有返回值（与其它协议包一致），因此它遇到失败只记日志；
/// 调用方的取消令牌被触发时按 .NET 惯例抛 <see cref="OperationCanceledException"/>。</para>
/// </remarks>
public sealed class MqttClient : IDisposable, IAsyncDisposable
{
    private readonly MqttSdkClient sdk;
    private readonly MqttConnectionStateMachine state;
    private readonly MqttLifecycleGate lifecycle = new();
    private readonly string endpoint;
    private readonly ILogger logger;
    private int disposed;

    private MqttClient(MqttClientOptions options, ILoggerFactory loggerFactory)
    {
        logger = loggerFactory.CreateLogger<MqttClient>();
        state = new MqttConnectionStateMachine(this, logger);
        endpoint = $"{options.Host}:{options.Port}";

        if (options.AllowUntrustedCertificates)
        {
            logger.LogWarning("MqttClientOptions.AllowUntrustedCertificates is enabled: any server certificate will be accepted.");
        }

        sdk = new MqttSdkClient(options, loggerFactory);
        sdk.MessageReceived += OnSdkMessageReceived;
    }

    /// <summary>按选项创建客户端，并在此时校验选项、取快照。</summary>
    /// <param name="options">连接选项；不能为 null。之后修改这个对象不影响已创建的客户端。</param>
    /// <param name="loggerFactory">日志工厂；为 null 时不输出日志。</param>
    /// <returns>未连接的客户端。</returns>
    /// <exception cref="ArgumentNullException"><paramref name="options"/> 为 null。</exception>
    /// <exception cref="ArgumentException">某个选项取值非法，消息写明属性名与允许范围。</exception>
    public static MqttClient Create(MqttClientOptions options, ILoggerFactory? loggerFactory = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        MqttOptionsValidator.Validate(options);

        return new MqttClient(options, loggerFactory ?? NullLoggerFactory.Instance);
    }

    /// <summary>当前连接状态。</summary>
    public MqttConnectionState State => state.Current;

    /// <summary>连接状态变化时触发；订阅者抛异常只记 Error，不影响状态，也不影响其余订阅者。</summary>
    public event EventHandler<MqttConnectionStateChangedEventArgs>? StateChanged
    {
        add => state.StateChanged += value;
        remove => state.StateChanged -= value;
    }

    /// <summary>收到消息时在 SDK 的接收回调线程上同步触发（P61-D8）。</summary>
    /// <remarks>订阅者全部返回之后 SDK 才发 QoS 确认，因此**订阅者不能阻塞**，否则会拖住后续消息与确认。</remarks>
    public event EventHandler<MqttMessageReceivedEventArgs>? MessageReceived;

    /// <summary>连接 Broker。</summary>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>已连接时成功。已是 <see cref="MqttConnectionState.Connected"/> 时直接返回成功；
    /// 正在连接或重连时返回 <see cref="CommErrorKind.InvalidRequest"/>。</returns>
    /// <exception cref="ObjectDisposedException">客户端已释放。</exception>
    public async Task<CommResult> ConnectAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();

        // 先看状态再看锁：已连接时不需要排队（P61-D4）。
        if (State == MqttConnectionState.Connected)
        {
            return CommResult.Success();
        }

        if (State is MqttConnectionState.Connecting or MqttConnectionState.Reconnecting)
        {
            return InProgress();
        }

        await using IAsyncDisposable lease = await lifecycle.EnterAsync(cancellationToken).ConfigureAwait(false);
        return await ConnectUnderLockAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>主动断开（发送 DISCONNECT，Broker 因此丢弃遗嘱）。可以重复调用。</summary>
    /// <param name="cancellationToken">取消令牌；被触发时抛 <see cref="OperationCanceledException"/>。</param>
    public async Task DisconnectAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();

        // 生命周期操作走同一把锁：断开会等当前这次连接尝试结束（P61-D10）。
        await using IAsyncDisposable lease = await lifecycle.EnterAsync(cancellationToken).ConfigureAwait(false);
        state.MoveTo(MqttConnectionState.Disconnected, "The client was disconnected by the application.");

        CommResult outcome = await sdk.DisconnectAsync(cancellationToken).ConfigureAwait(false);
        if (!outcome.IsSuccess)
        {
            logger.LogWarning("The MQTT client was not disconnected cleanly: {Outcome}.", outcome);
        }
    }

    /// <summary>发布一条消息。</summary>
    /// <param name="message">要发布的消息。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>发布结果。</returns>
    // Task 3 的占位实现；Task 4 用真实实现整体替换，并删除这段注释。
    public Task<CommResult> PublishAsync(MqttMessage message, CancellationToken cancellationToken = default) =>
        Task.FromResult(CommResult.Fail("Publishing is not implemented yet.", CommErrorKind.NotSupported));

    /// <summary>订阅一个主题过滤器。</summary>
    /// <param name="topicFilter">主题过滤器。</param>
    /// <param name="qos">请求的 QoS。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>Broker 授予的 QoS，或失败原因。</returns>
    // Task 3 的占位实现；Task 4 用真实实现整体替换，并删除这段注释。
    public Task<CommResult<MqttQos>> SubscribeAsync(string topicFilter, MqttQos qos = MqttQos.AtMostOnce, CancellationToken cancellationToken = default) =>
        Task.FromResult(CommResult<MqttQos>.Fail("Subscribing is not implemented yet.", CommErrorKind.NotSupported));

    /// <summary>退订一个主题过滤器。</summary>
    /// <param name="topicFilter">主题过滤器。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>退订结果。</returns>
    // Task 3 的占位实现；Task 4 用真实实现整体替换，并删除这段注释。
    public Task<CommResult> UnsubscribeAsync(string topicFilter, CancellationToken cancellationToken = default) =>
        Task.FromResult(CommResult.Fail("Unsubscribing is not implemented yet.", CommErrorKind.NotSupported));

    /// <summary>释放客户端。可以重复调用。</summary>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0)
        {
            return;
        }

        state.MoveTo(MqttConnectionState.Disconnected, "The client was disposed.");
        sdk.DisposeAsync().AsTask().GetAwaiter().GetResult();
        lifecycle.Dispose();
    }

    /// <summary>异步释放客户端。可以重复调用。</summary>
    /// <returns>释放完成。</returns>
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0)
        {
            return;
        }

        state.MoveTo(MqttConnectionState.Disconnected, "The client was disposed.");
        await sdk.DisposeAsync().ConfigureAwait(false);
        lifecycle.Dispose();
    }

    private async Task<CommResult> ConnectUnderLockAsync(CancellationToken cancellationToken)
    {
        // 排队期间状态可能已经变了，因此进锁后再判一次（P61-D10）。
        if (State == MqttConnectionState.Connected)
        {
            return CommResult.Success();
        }

        if (State is MqttConnectionState.Connecting or MqttConnectionState.Reconnecting)
        {
            return InProgress();
        }

        state.MoveTo(MqttConnectionState.Connecting, null);
        Stopwatch timer = Stopwatch.StartNew();

        // 首次连接失败不启动后台重连（P6-C11）；首次失败由调用方决定要不要重试。
        (CommResult outcome, bool _) = await sdk.ConnectAsync(cancellationToken).ConfigureAwait(false);
        timer.Stop();

        state.MoveTo(outcome.IsSuccess ? MqttConnectionState.Connected : MqttConnectionState.Disconnected, outcome.ErrorMessage);
        if (outcome.IsSuccess)
        {
            logger.LogInformation("Connected to the MQTT broker at {Endpoint} in {Elapsed} ms.", endpoint, timer.ElapsedMilliseconds);
        }
        else
        {
            logger.LogWarning("The MQTT connection attempt failed after {Elapsed} ms: {Outcome}.", timer.ElapsedMilliseconds, outcome);
        }

        return outcome;
    }

    private void OnSdkMessageReceived(MqttMessage message) =>
        EventSubscribers.InvokeEach(
            MessageReceived,
            this,
            new MqttMessageReceivedEventArgs(message),
            logger,
            nameof(MessageReceived));

    private static CommResult InProgress() => CommResult.Fail(
        "A connection attempt is already in progress.",
        CommErrorKind.InvalidRequest);

    private void ThrowIfDisposed()
    {
        if (Volatile.Read(ref disposed) != 0)
        {
            throw new ObjectDisposedException(nameof(MqttClient));
        }
    }
}