using Junevy.Communication.Channels;
using Junevy.Communication.Core.Diagnostics;
using Junevy.Communication.Core.Results;
using Junevy.Communication.Secs.Protocol;
using Junevy.Communication.Tcp;
using Junevy.Communication.Tcp.Server;
using Microsoft.Extensions.Logging;

namespace Junevy.Communication.Secs.Hsms;

/// <summary>
/// 设备端启动的两条路径（计划 Task 7，P51-D9、D11）：被动端监听等主机来连，主动端去连主机并发起 Select。
/// 两条路径只有"通道类型"、"握手钩子"与"取得当前连接的方式"不同，通道组件的分帧与关联完全一样。
/// </summary>
public sealed partial class HsmsEquipment
{
    /// <summary>不发 Linktest 时心跳用的占位间隔（不会被用到，只是给出一个合法值）。</summary>
    private const int PlaceholderHeartbeatInterval = 1000;

    /// <summary>主动端建立 TCP 连接的上限；握手窗口（T6）在它之后才开始计时。</summary>
    private const int ConnectTimeout = 5000;

    // 被动端：TcpServer + 被动端握手 + Linktest 探测；组成通道配置时按 P51-D9 映射计时器。
    private async Task<CommResult> StartListeningAsync(CancellationToken cancellationToken)
    {
        HsmsEquipmentTimers timers = settings.Timers;
        var config = new TcpServerConfig
        {
            ListenAddress = settings.Address!,
            Port = settings.Port,
            MaxSessions = SingleSession,
            SessionHandshakeTimeout = timers.T7,
            PartialFrameTimeout = timers.T8,
            Correlation = CorrelationMode.Keyed,
            ResetOnRequestTimeout = false,
            Heartbeat = HsmsHeartbeat(timers),
        };

        // 分帧与关联由 HSMS 自己提供，服务端配置里的 Framing / Correlation 保持传输层的默认值。
        var components = new TcpChannelComponents
        {
            FrameCodec = HsmsFraming.Create(timers.MaxMessageLength),
            KeyExtractor = new HsmsKeyExtractor(),
            Initializer = new HsmsPassiveHandshake(session, timers, logger),
            HealthProbeFactory = channel => new HsmsLinktestProbe(channel, timers),
        };

        // 通道的日志（连接、握手、心跳、会话失败）与设备端用同一个日志工厂，宿主才看得到链路层的原因。
        var listening = new TcpServer(config, loggerFactory.CreateLogger<TcpServer>(), components);
        listening.SessionConnected += OnSessionConnected;
        listening.SessionClosed += OnSessionClosed;

        lock (sync)
        {
            server = listening;
        }

        CommResult result = await listening.StartAsync(cancellationToken).ConfigureAwait(false);
        if (!result.IsSuccess)
            await ReleaseAsync(listening, null).ConfigureAwait(false);

        return result;
    }

    // 主动端：TcpClientChannel + 主动端握手 + Linktest 探测；派发、发送与状态完全复用被动端那一套，
    // 只有"取得当前连接"与"断开的方式"不同（Q26：不允许两份派发代码）。
    private async Task<CommResult> StartConnectingAsync(CancellationToken cancellationToken)
    {
        HsmsEquipmentTimers timers = settings.Timers;

        // P51-D9：T6 → 握手超时、T5 → 固定间隔重连、T8 → 字符间隔超时。
        // T3 超时是事务失败，不是链路失败：Keyed 关联下请求超时本来就不重建连接（ResetOnRequestTimeout 只作用于
        // Sequential），这里仍显式写 false，免得日后换关联方式时把 T3 超时变成断链。被动端同理。
        var config = new TcpClientChannelConfig
        {
            Host = settings.Address!,
            Port = settings.Port,
            ConnectTimeout = ConnectTimeout,
            HandshakeTimeout = timers.T6,
            PartialFrameTimeout = timers.T8,
            Correlation = CorrelationMode.Keyed,
            ResetOnRequestTimeout = false,
            Heartbeat = HsmsHeartbeat(timers),
            // OnInitialFailure：Separate.req 之后的再连若失败，也交给通道按 T5 继续重连（见 ReconnectAfterSeparateAsync）。
            // 首次启动失败时通道随即被释放，因此 StartAsync 的"打开失败就返回失败"不受影响。
            Reconnect = new ReconnectOptions
            {
                Enabled = true,
                Mode = ReconnectMode.FixedInterval,
                Interval = timers.T5,
                OnInitialFailure = true,
            },
        };

        var components = new TcpChannelComponents
        {
            FrameCodec = HsmsFraming.Create(timers.MaxMessageLength),
            KeyExtractor = new HsmsKeyExtractor(),
            Initializer = new HsmsActiveHandshake(session, timers, logger),
            HealthProbeFactory = channel => new HsmsLinktestProbe(channel, timers),
        };

        var connecting = new TcpClientChannel(config, loggerFactory.CreateLogger<TcpClientChannel>(), components);
        connecting.FrameReceived += OnFrameReceived;
        connecting.StateChanged += OnClientStateChanged;

        // 主动端的当前连接就是这条通道：不等 Connected 事件再挂（事件排队送达，主机的第一条主消息可能先到），
        // 链路不在时通道的发送本身就返回失败结果。
        lock (sync)
        {
            client = connecting;
            current = connecting;
        }

        // 握手失败（Select 被拒、T6 超时、连不上）就是"打开失败"：不在这里重试，
        // 重连由通道按 T5 做；之后再 StartAsync 即可（P51-D19：不把异常漏给调用方）。
        CommResult result = await connecting.ConnectAsync(cancellationToken).ConfigureAwait(false);
        if (!result.IsSuccess)
        {
            await ReleaseAsync(null, connecting).ConfigureAwait(false);
            session.Reset($"The equipment could not connect: {result.ErrorMessage}");
            logger.LogWarning("The HSMS equipment did not connect: {Result}", result);
        }

        return result;
    }

    // 主动端收到 Separate.req：断开（P51-D12）。DisconnectAsync 会让通道永久停止重连，
    // 而设备端仍处于启动状态，所以等 T5 之后自己再连一次；再失败由通道按 T5 继续（OnInitialFailure）。
    // 停止或释放会取消 token，这时既不等满 T5 也不再连接。
    private async Task ReconnectAfterSeparateAsync(IConnectable client, CancellationToken token)
    {
        await client.DisconnectAsync(CancellationToken.None).ConfigureAwait(false);
        try
        {
            await Task.Delay(settings.Timers.T5, token).ConfigureAwait(false);
            CommResult reconnected = await client.ConnectAsync(token).ConfigureAwait(false);
            if (!reconnected.IsSuccess)
                logger.LogWarning("Reconnecting after Separate.req failed; the channel keeps retrying every T5: {Result}", reconnected);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            logger.LogDebug("The equipment stopped before reconnecting after Separate.req.");
        }
    }

    // 收尾（启动失败与停止共用）：解订阅、断开、释放。主动端先 DisconnectAsync（标记"用户主动断开"，
    // 重连循环因此永久停下）再释放。字段只在仍指向被释放的对象时才清空：停止之后可能已经开始了新的一轮启动。
    private async Task ReleaseAsync(ITcpServer? listening, TcpClientChannel? connecting)
    {
        if (listening != null)
        {
            listening.SessionConnected -= OnSessionConnected;
            listening.SessionClosed -= OnSessionClosed;
            await listening.DisposeAsync().ConfigureAwait(false);
        }

        if (connecting != null)
        {
            connecting.FrameReceived -= OnFrameReceived;
            connecting.StateChanged -= OnClientStateChanged;
            await connecting.DisconnectAsync().ConfigureAwait(false);
            await connecting.DisposeAsync().ConfigureAwait(false);
        }

        lock (sync)
        {
            if (ReferenceEquals(server, listening))
                server = null;
            if (ReferenceEquals(client, connecting))
            {
                client = null;
                if (ReferenceEquals(current, connecting))
                    current = null;
            }
        }
    }

    // P51-D9：LinktestInterval > 0 才启用心跳；间隔用它、超时用 T6、MaxFailures = 1、只在空闲时探测。
    private HeartbeatOptions HsmsHeartbeat(HsmsEquipmentTimers timers) => new HeartbeatOptions
    {
        Enabled = timers.LinktestInterval > 0,
        Interval = timers.LinktestInterval > 0 ? timers.LinktestInterval : PlaceholderHeartbeatInterval,
        Timeout = timers.T6,
        MaxFailures = 1,
        OnlyWhenIdle = true,
    };
}