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

        var listening = new TcpServer(config, null, components);
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

        // P51-D9：T6 → 握手超时、T5 → 固定间隔重连、T8 → 字符间隔超时；
        // ResetOnRequestTimeout = false 是因为 T3 超时是事务失败，不是链路失败。
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
            Reconnect = new ReconnectOptions
            {
                Enabled = true,
                Mode = ReconnectMode.FixedInterval,
                Interval = timers.T5,
            },
        };

        var components = new TcpChannelComponents
        {
            FrameCodec = HsmsFraming.Create(timers.MaxMessageLength),
            KeyExtractor = new HsmsKeyExtractor(),
            Initializer = new HsmsActiveHandshake(session, timers, logger),
            HealthProbeFactory = channel => new HsmsLinktestProbe(channel, timers),
        };

        var connecting = new TcpClientChannel(config, null, components);
        connecting.FrameReceived += OnFrameReceived;
        connecting.StateChanged += OnClientStateChanged;
        lock (sync)
        {
            client = connecting;
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

    // 启动失败时把半成品收干净：解订阅、断开、释放，字段也复位。
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
            await connecting.DisposeAsync().ConfigureAwait(false);
        }

        lock (sync)
        {
            server = null;
            client = null;
            current = null;
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