using System.Net;
using Junevy.Communication.Channels;
using Junevy.Communication.Channels.Lifecycle;
using Junevy.Communication.Channels.Pipeline;
using Junevy.Communication.Core.Results;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Junevy.Communication.Udp;

/// <summary>
/// UDP 客户端通道（设计文档第 9 节，计划 15.1）。直接组合 <see cref="ConnectionSupervisor"/>（生命周期、后台重连）、
/// <see cref="DatagramConnectionDriver"/>（绑定、握手、心跳）与 <see cref="DatagramChannel"/>（收发、请求与迟到应答），不继承 <see cref="StreamClientChannel"/>。
/// </summary>
/// <remarks>
/// "已连接"表示套接字已绑定（设计文档 5.1 节）。UDP 无法感知对端是否在线，需配合心跳或空闲超时才能发现对端掉线。
/// 定向模式（配置了 RemoteHost）下发送与请求发往远端，只派发来自远端的数据报；非定向模式使用 <see cref="SendToAsync"/> 与 <see cref="RequestToAsync"/>。
/// 构造时校验配置（D5）：非法值抛出 <see cref="ArgumentException"/> 族，调用方的配置对象不会被修改。运行行为只依赖构造时的快照；<see cref="Config"/> 返回的是构造时传入的对象本身。
/// 事件在线程池线程上触发。同步 <see cref="Dispose"/> 最长等待 <c>DisconnectTimeout + 1000</c> 毫秒（D16）。
/// </remarks>
public sealed class UdpChannel : IUdpChannel
{
    private readonly string name;
    private readonly UdpChannelConfig config;
    private readonly ILogger logger;
    private readonly ConnectionStatistics statistics = new ConnectionStatistics();
    private readonly UdpChannelSettings settings;
    private readonly DatagramClientOptions resolved;
    private readonly DatagramConnectionDriver driver;
    private readonly ConnectionSupervisor supervisor;
    private int disposed;

    /// <summary>
    /// 创建 UDP 客户端通道，名称默认为 <c>udp://本地地址:端口</c>；定向模式追加 <c>-&gt;远端:端口</c>。
    /// </summary>
    /// <param name="config">UDP 配置；构造时校验，之后的修改不影响通道。</param>
    /// <param name="logger">日志记录器；为 null 时不记录日志。</param>
    /// <param name="components">代码级覆盖（关联、握手、心跳、重连）；为 null 时只使用配置。</param>
    /// <exception cref="ArgumentNullException">配置为 null。</exception>
    /// <exception cref="ArgumentException">配置非法。</exception>
    public UdpChannel(UdpChannelConfig config, ILogger<UdpChannel>? logger = null, ChannelComponents? components = null)
        : this(DefaultName(config), config, logger, components)
    {
    }

    /// <summary>
    /// 创建具有指定名称的 UDP 客户端通道。
    /// </summary>
    /// <param name="name">通道名称（与通道工厂中的名称一致）。</param>
    /// <param name="config">UDP 配置；构造时校验，之后的修改不影响通道。</param>
    /// <param name="logger">日志记录器；为 null 时不记录日志。</param>
    /// <param name="components">代码级覆盖（关联、握手、心跳、重连）；为 null 时只使用配置。</param>
    /// <exception cref="ArgumentNullException">名称或配置为 null。</exception>
    /// <exception cref="ArgumentException">配置非法。</exception>
    public UdpChannel(string name, UdpChannelConfig config, ILogger<UdpChannel>? logger = null, ChannelComponents? components = null)
    {
        this.name = name ?? throw new ArgumentNullException(nameof(name));
        this.config = config ?? throw new ArgumentNullException(nameof(config));
        this.logger = logger ?? NullLogger<UdpChannel>.Instance;

        settings = UdpChannelSettings.From(config, components);
        resolved = new DatagramClientOptions(settings.Client, components, this);
        driver = new DatagramConnectionDriver(resolved, this, token => UdpDatagramTransport.OpenAsync(settings, token), RaiseFrameReceived,
                                              () => name, statistics, this.logger);
        supervisor = new ConnectionSupervisor(name, driver, resolved.ReconnectPolicy, resolved.ReconnectOnInitialFailure, statistics, this.logger);
        driver.Attach(supervisor);
        supervisor.StateChanged += OnSupervisorStateChanged;
    }

    /// <inheritdoc />
    public UdpChannelConfig Config => config;

    /// <inheritdoc />
    public string Name => name;

    /// <inheritdoc />
    public ConnectionState State => supervisor.State;

    /// <inheritdoc />
    public bool IsConnected => State == ConnectionState.Connected;

    /// <inheritdoc />
    public ConnectionStatistics Statistics => statistics;

    /// <inheritdoc />
    public IPEndPoint? LocalEndPoint => ConnectedChannel()?.LocalEndPoint as IPEndPoint;

    /// <inheritdoc />
    public event EventHandler<ConnectionStateChangedEventArgs>? StateChanged;

    /// <inheritdoc />
    public event EventHandler<FrameReceivedEventArgs>? FrameReceived;

    /// <inheritdoc />
    public async Task<CommResult> ConnectAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        return await supervisor.ConnectAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public Task DisconnectAsync(CancellationToken cancellationToken = default)
    {
        if (IsDisposed)
            return Task.CompletedTask;

        return supervisor.DisconnectAsync(resolved.DisconnectTimeout, cancellationToken);
    }

    /// <inheritdoc />
    public async Task<bool> WaitForConnectedAsync(int timeout, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        return await supervisor.WaitForConnectedAsync(timeout, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// 发送一个数据报到远端（定向模式）。非定向模式返回 <c>InvalidRequest</c>，请使用 <see cref="SendToAsync"/>。
    /// </summary>
    /// <param name="payload">负载；可以为空。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>发送结果。</returns>
    public async Task<CommResult> SendAsync(ReadOnlyMemory<byte> payload, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        if (!IsDirected)
            return CommResult.Fail("Remote endpoint is required; use SendToAsync.", CommErrorKind.InvalidRequest);

        DatagramChannel? channel = ConnectedChannel();
        if (channel == null)
            return CommResult.Fail("The channel is not connected.", CommErrorKind.NotConnected);

        return await channel.SendAsync(payload, channel.DirectedRemote, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// 向远端发送请求并等待应答（定向模式）。非定向模式返回 <c>InvalidRequest</c>，请使用 <see cref="RequestToAsync"/>。
    /// </summary>
    /// <param name="payload">请求负载，不能为空。</param>
    /// <param name="options">超时与匹配器；为 null 时使用配置的 RequestTimeout。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>应答或失败结果。</returns>
    public async Task<CommResult<byte[]>> RequestAsync(ReadOnlyMemory<byte> payload, RequestOptions? options = null,
                                                       CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        if (!IsDirected)
            return CommResult<byte[]>.Fail("Remote endpoint is required; use RequestToAsync.", CommErrorKind.InvalidRequest);

        DatagramChannel? channel = ConnectedChannel();
        if (channel == null)
            return CommResult<byte[]>.Fail("The channel is not connected.", CommErrorKind.NotConnected);

        return await channel.RequestAsync(payload, options, channel.DirectedRemote, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// 等待下一个入站数据报（任何来源；定向模式下只有远端的数据报）。
    /// </summary>
    /// <param name="options">超时与匹配器；为 null 时使用默认值。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>数据报负载或失败结果。</returns>
    public async Task<CommResult<byte[]>> ReceiveAsync(RequestOptions? options = null, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        DatagramChannel? channel = ConnectedChannel();
        if (channel == null)
            return CommResult<byte[]>.Fail("The channel is not connected.", CommErrorKind.NotConnected);

        return await channel.ReceiveAsync(options, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<CommResult> SendToAsync(IPEndPoint remote, ReadOnlyMemory<byte> payload, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        if (remote == null)
            throw new ArgumentNullException(nameof(remote));
        if (remote.Port == 0)
            return CommResult.Fail("The remote port must not be 0.", CommErrorKind.InvalidRequest);

        DatagramChannel? channel = ConnectedChannel();
        if (channel == null)
            return CommResult.Fail("The channel is not connected.", CommErrorKind.NotConnected);

        return await channel.SendAsync(payload, remote, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<CommResult<byte[]>> RequestToAsync(IPEndPoint remote, ReadOnlyMemory<byte> payload, RequestOptions? options = null,
                                                         CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        if (remote == null)
            throw new ArgumentNullException(nameof(remote));
        if (remote.Port == 0)
            return CommResult<byte[]>.Fail("The remote port must not be 0.", CommErrorKind.InvalidRequest);

        DatagramChannel? channel = ConnectedChannel();
        if (channel == null)
            return CommResult<byte[]>.Fail("The channel is not connected.", CommErrorKind.NotConnected);

        // 定向模式只派发远端的数据报，对其他地址的请求永远收不到应答：在发送之前拒绝。
        if (channel.DirectedRemote != null && !channel.DirectedRemote.Equals(remote))
            return CommResult<byte[]>.Fail("A directed channel receives replies only from its configured remote endpoint.", CommErrorKind.InvalidRequest);

        return await channel.RequestAsync(payload, options, remote, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// 同步释放：等待 <see cref="DisposeAsync"/> 完成，最长 <c>DisconnectTimeout + 1000</c> 毫秒（D16）。
    /// 超时后返回，释放在后台继续。在事件处理器内调用时不会死锁。
    /// </summary>
    public void Dispose()
    {
        int limit = resolved.DisconnectTimeout + 1000;
        Task disposing = DisposeAsync().AsTask();
        if (!disposing.Wait(limit))
            logger.LogWarning("Disposing the channel {Name} did not finish within {Timeout} ms; the release continues in the background.", name, limit);
    }

    /// <summary>
    /// 异步释放：关闭套接字、停止重连与心跳、结束状态事件派发。幂等。
    /// 在状态事件处理器内调用时只发出停止信号，不等待状态事件派发结束，因此不会死锁。
    /// </summary>
    /// <returns>释放完成的任务。</returns>
    public ValueTask DisposeAsync()
    {
        Interlocked.Exchange(ref disposed, 1);
        return supervisor.DisposeAsync();
    }

    // 是否为定向模式：以构造时的快照为准。
    private bool IsDirected => settings.RemoteHost != null;

    private bool IsDisposed => Volatile.Read(ref disposed) != 0;

    private void ThrowIfDisposed()
    {
        if (IsDisposed)
            throw new ObjectDisposedException(GetType().Name);
    }

    // 只有 Connected 时才返回数据报通道；连接正在关闭或重建时即使有旧的通道对象也返回 null。
    private DatagramChannel? ConnectedChannel()
        => supervisor.State == ConnectionState.Connected ? driver.CurrentChannel : null;

    // 监督器的状态事件经此转发：发送者为通道本身，订阅者逐个隔离异常。
    private void OnSupervisorStateChanged(object? sender, ConnectionStateChangedEventArgs args)
        => LifecycleSupport.InvokeEach(StateChanged, this, args, logger, "StateChanged");

    // 数据报经路由派发给订阅者：发送者为通道本身，订阅者逐个隔离异常。
    private Task RaiseFrameReceived(FrameReceivedEventArgs args)
    {
        FrameRouter.RaiseToSubscribers(FrameReceived, this, args, logger);
        return Task.CompletedTask;
    }

    private static string DefaultName(UdpChannelConfig config)
    {
        if (config == null)
            throw new ArgumentNullException(nameof(config));

        string local = FormatEndPoint(config.LocalAddress, config.LocalPort);
        return string.IsNullOrWhiteSpace(config.RemoteHost)
            ? $"udp://{local}"
            : $"udp://{local}->{FormatEndPoint(config.RemoteHost, config.RemotePort)}";
    }

    private static string FormatEndPoint(string? host, int port)
        => host != null && host.IndexOf(':') >= 0 ? $"[{host}]:{port}" : $"{host}:{port}";
}
