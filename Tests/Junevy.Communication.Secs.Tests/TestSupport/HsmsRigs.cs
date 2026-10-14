using Junevy.Communication.Channels;
using Junevy.Communication.Core.Results;
using Junevy.Communication.Secs.Protocol;
using Junevy.Communication.Testing;
using Junevy.Communication.Tcp;
using Microsoft.Extensions.Logging;

namespace Junevy.Communication.Secs.Tests;

/// <summary>
/// 被动端设备（监听）一侧的真实装置：<c>TcpServer</c> + <see cref="HsmsPassiveHandshake"/>，
/// 按 P51-D9 的映射配置计时器、关联与分帧。
/// </summary>
internal sealed class HsmsPassiveRig : IAsyncDisposable
{
    /// <summary>会话请求的默认超时（毫秒），与握手等待无关。</summary>
    private const int HandshakeUnrelatedRequestTimeout = 400;

    private readonly TcpServer server;

    private HsmsPassiveRig(TcpServer server, HsmsSessionState session, TestLogger logger, HsmsStateLog states, int port)
    {
        this.server = server;
        Session = session;
        Logger = logger;
        States = states;
        Port = port;
    }

    /// <summary>监听的端口。</summary>
    internal int Port { get; }

    /// <summary>HSMS 会话状态。</summary>
    internal HsmsSessionState Session { get; }

    /// <summary>状态变化记录（已订阅状态变化事件）。</summary>
    internal HsmsStateLog States { get; }

    /// <summary>日志；断言"是否记了 Warning"时使用。</summary>
    internal TestLogger Logger { get; }

    /// <summary>服务端；断言会话数时使用。</summary>
    internal TcpServer Server => server;

    /// <summary>起一个被动端设备并开始监听。</summary>
    /// <param name="timers">计时器快照。</param>
    /// <returns>已监听的设备；调用方负责释放。</returns>
    internal static async Task<HsmsPassiveRig> StartAsync(HsmsEquipmentTimers timers)
    {
        var logger = new TestLogger();
        var session = new HsmsSessionState(logger);
        var states = new HsmsStateLog();
        session.StateChanged += states.Record;

        // T7 → 会话握手超时、T8 → 字符间隔超时、MaxSessions = 1（HSMS-SS 单会话）；
        // ResetOnRequestTimeout = false 是因为 T3 超时是事务失败，不是链路失败。
        // RequestTimeout 调小到 400：握手等帧不受它约束（用例专门验证这一点），调小是为了让那条用例跑得快。
        var config = new TcpServerConfig
        {
            ListenAddress = LoopbackTcp.Host,
            Port = HsmsPorts.Free(),
            MaxSessions = 1,
            SessionHandshakeTimeout = timers.T7,
            PartialFrameTimeout = timers.T8,
            RequestTimeout = HandshakeUnrelatedRequestTimeout,
            Correlation = CorrelationMode.Keyed,
            ResetOnRequestTimeout = false,
            Heartbeat = HsmsChannelParts.Heartbeat(timers),
        };

        var rig = new HsmsPassiveRig(
            new TcpServer(config, logger.CreateLogger<TcpServer>(), HsmsChannelParts.Components(timers, new HsmsPassiveHandshake(session, timers, logger))),
            session,
            logger,
            states,
            config.Port);

        // 与设备端一样：Selected 由服务端报告会话时给出，会话关闭交给会话状态判断（事件可能晚到）。
        rig.server.SessionConnected += (sender, args) => session.LinkAttached(args.Session.IsConnected, "The session connected.");
        rig.server.SessionClosed += (sender, args) => session.LinkLost(stillConnected: false, "The session closed.");

        CommResult started = await Arrival.WithinAsync(rig.server.StartAsync(), 10000).ConfigureAwait(false);
        if (!started.IsSuccess)
            throw new InvalidOperationException($"The passive rig did not start: {started.ErrorMessage}");

        return rig;
    }

    /// <summary>停止监听并释放服务端。可重复调用。</summary>
    public async ValueTask DisposeAsync() => await server.DisposeAsync().ConfigureAwait(false);
}

/// <summary>
/// 主动端设备（去连主机）一侧的真实装置：<c>TcpClientChannel</c> + <see cref="HsmsActiveHandshake"/>，
/// 按 P51-D9 的映射配置计时器、关联与分帧。
/// </summary>
internal sealed class HsmsActiveRig : IAsyncDisposable
{
    private HsmsActiveRig(TcpClientChannel channel, HsmsSessionState session, TestLogger logger, HsmsStateLog states)
    {
        Channel = channel;
        Session = session;
        Logger = logger;
        States = states;
    }

    /// <summary>客户端通道。</summary>
    internal TcpClientChannel Channel { get; }

    /// <summary>HSMS 会话状态。</summary>
    internal HsmsSessionState Session { get; }

    /// <summary>状态变化记录（已订阅状态变化事件）。</summary>
    internal HsmsStateLog States { get; }

    /// <summary>日志；断言"是否记了 Warning"时使用。</summary>
    internal TestLogger Logger { get; }

    /// <summary>建一个主动端设备（尚未连接）。</summary>
    /// <param name="port">主机的监听端口。</param>
    /// <param name="timers">计时器快照。</param>
    /// <returns>设备；调用方负责释放。</returns>
    internal static HsmsActiveRig Start(int port, HsmsEquipmentTimers timers)
    {
        var logger = new TestLogger();
        var session = new HsmsSessionState(logger);
        var states = new HsmsStateLog();
        session.StateChanged += states.Record;

        // P51-D9：T6 → 握手超时、T5 → 固定间隔重连、T8 → 字符间隔超时。
        var config = new TcpClientChannelConfig
        {
            Host = LoopbackTcp.Host,
            Port = port,
            ConnectTimeout = 5000,
            HandshakeTimeout = timers.T6,
            PartialFrameTimeout = timers.T8,
            Correlation = CorrelationMode.Keyed,
            ResetOnRequestTimeout = false,
            Heartbeat = HsmsChannelParts.Heartbeat(timers),
            Reconnect = new ReconnectOptions
            {
                Enabled = true,
                Mode = ReconnectMode.FixedInterval,
                Interval = timers.T5,
            },
        };

        var channel = new TcpClientChannel(config, logger.CreateLogger<TcpClientChannel>(), HsmsChannelParts.Components(timers, new HsmsActiveHandshake(session, timers, logger)));

        // 与设备端一样：通道的状态事件交给会话状态判断，Selected 在通道报告 Connected 时给出。
        channel.StateChanged += (sender, args) => session.ApplyClientEvent(args, channel.State);
        return new HsmsActiveRig(channel, session, logger, states);
    }

    /// <summary>断开并释放通道。可重复调用。</summary>
    public async ValueTask DisposeAsync() => await Channel.DisposeAsync().ConfigureAwait(false);
}
