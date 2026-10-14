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
/// <see cref="ArgumentNullException"/>；释放之后 <see cref="ConnectAsync"/> 与 <see cref="DisconnectAsync"/> 抛
/// <see cref="ObjectDisposedException"/>，发布、订阅、退订返回 <see cref="CommErrorKind.ConnectionClosed"/>。
/// <see cref="Dispose"/> 与 <see cref="DisposeAsync"/> 可以重复调用。</para>
/// <para><see cref="DisconnectAsync"/> 没有返回值（与其它协议包一致），因此它遇到失败只记日志；
/// 调用方的取消令牌被触发时按 .NET 惯例抛 <see cref="OperationCanceledException"/>。</para>
/// <para>本类只是门面：连接、断开与释放在 <see cref="MqttLifecycle"/>，发布、订阅与退订在 <see cref="MqttMessaging"/>，
/// 两者的并发规则不同（P61-D10）。</para>
/// </remarks>
public sealed class MqttClient : IDisposable, IAsyncDisposable
{
    private readonly MqttConnectionStateMachine state;
    private readonly MqttMessaging messaging;
    private readonly MqttReconnectController reconnect;
    private readonly MqttLifecycle lifecycle;
    private readonly ILogger logger;

    private MqttClient(MqttClientOptions options, ILoggerFactory loggerFactory)
    {
        logger = loggerFactory.CreateLogger<MqttClient>();
        state = new MqttConnectionStateMachine(this, logger);

        if (options.AllowUntrustedCertificates)
        {
            logger.LogWarning("MqttClientOptions.AllowUntrustedCertificates is enabled: any server certificate will be accepted.");
        }

        var sdk = new MqttSdkClient(options, loggerFactory);
        var gate = new MqttLifecycleGate();
        messaging = new MqttMessaging(sdk, state, new MqttSubscriptionTable(), options.ProtocolVersion, logger);
        reconnect = new MqttReconnectController(
            sdk,
            state,
            messaging,
            gate,
            options.AutoReconnect,
            options.ReconnectInterval,
            options.MaxReconnectInterval,
            logger);
        lifecycle = new MqttLifecycle(sdk, state, reconnect, gate, $"{options.Host}:{options.Port}", logger);

        sdk.MessageReceived += OnSdkMessageReceived;
        sdk.Disconnected += OnUnexpectedDisconnect;
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
    /// <param name="cancellationToken">取消令牌；被触发时返回 <see cref="CommErrorKind.Cancelled"/>。</param>
    /// <returns>已连接时成功。已是 <see cref="MqttConnectionState.Connected"/> 时直接返回成功；
    /// 正在连接或重连时返回 <see cref="CommErrorKind.InvalidRequest"/>；连接期间客户端被释放时返回
    /// <see cref="CommErrorKind.ConnectionClosed"/>。</returns>
    /// <exception cref="ObjectDisposedException">客户端已释放。</exception>
    public async Task<CommResult> ConnectAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        return await lifecycle.ConnectAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>主动断开（发送 DISCONNECT，Broker 因此丢弃遗嘱）。可以重复调用。</summary>
    /// <remarks>只结束当前这一轮重连：之后再 <see cref="ConnectAsync"/>，下一次意外断线照样重连。</remarks>
    /// <param name="cancellationToken">取消令牌；被触发时抛 <see cref="OperationCanceledException"/>。</param>
    /// <returns>断开完成。</returns>
    /// <exception cref="ObjectDisposedException">客户端已释放。</exception>
    public async Task DisconnectAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        await lifecycle.DisconnectAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>发布一条消息。</summary>
    /// <param name="message">要发布的消息。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>QoS 0 写出即成功，QoS 1 等 PUBACK，QoS 2 等 PUBCOMP（P61-D6）。</returns>
    /// <exception cref="ArgumentNullException"><paramref name="message"/> 为 null。</exception>
    public async Task<CommResult> PublishAsync(MqttMessage message, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(message);
        return lifecycle.IsDisposed
            ? MqttLifecycle.Disposed()
            : await messaging.PublishAsync(message, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>订阅一个主题过滤器。</summary>
    /// <param name="topicFilter">主题过滤器；不能为 null。</param>
    /// <param name="qos">请求的 QoS。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>Broker 授予的 QoS，或失败原因。成功的订阅会记入订阅表，供重连后恢复。</returns>
    /// <exception cref="ArgumentNullException"><paramref name="topicFilter"/> 为 null。</exception>
    public async Task<CommResult<MqttQos>> SubscribeAsync(
        string topicFilter,
        MqttQos qos = MqttQos.AtMostOnce,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(topicFilter);
        return lifecycle.IsDisposed
            ? MqttLifecycle.Disposed().As<MqttQos>()
            : await messaging.SubscribeAsync(topicFilter, qos, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>退订一个主题过滤器。</summary>
    /// <param name="topicFilter">主题过滤器；不能为 null。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>退订结果。成功后才把过滤器移出订阅表。</returns>
    /// <exception cref="ArgumentNullException"><paramref name="topicFilter"/> 为 null。</exception>
    public async Task<CommResult> UnsubscribeAsync(string topicFilter, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(topicFilter);
        return lifecycle.IsDisposed
            ? MqttLifecycle.Disposed()
            : await messaging.UnsubscribeAsync(topicFilter, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>释放客户端。可以重复调用。正在进行的连接会被取消，释放等它退出之后才返回。</summary>
    public void Dispose() => DisposeAsync().AsTask().GetAwaiter().GetResult();

    /// <summary>异步释放客户端。可以重复调用。正在进行的连接会被取消，释放等它退出之后才完成。</summary>
    /// <returns>释放完成。</returns>
    public ValueTask DisposeAsync() => lifecycle.DisposeAsync();

    private void OnSdkMessageReceived(MqttMessage message) =>
        EventSubscribers.InvokeEach(
            MessageReceived,
            this,
            new MqttMessageReceivedEventArgs(message),
            logger,
            nameof(MessageReceived));

    // 释放之后不再响应 SDK 的断线事件（P6-C8）。
    private void OnUnexpectedDisconnect(bool clientWasConnected, string? reason)
    {
        if (!lifecycle.IsDisposed)
        {
            reconnect.OnUnexpectedDisconnect(clientWasConnected, reason);
        }
    }

    private void ThrowIfDisposed()
    {
        if (lifecycle.IsDisposed)
        {
            throw new ObjectDisposedException(nameof(MqttClient));
        }
    }
}
