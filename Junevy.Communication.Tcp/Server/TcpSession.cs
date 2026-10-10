using System.Collections.Concurrent;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using Junevy.Communication.Channels;
using Junevy.Communication.Channels.Lifecycle;
using Junevy.Communication.Channels.Pipeline;
using Junevy.Communication.Core.Results;
using Microsoft.Extensions.Logging;

namespace Junevy.Communication.Tcp.Server;

/// <summary>
/// 服务端的一个 TCP 会话（内部）。持有接入的套接字、可选的 TLS 流、字节流通道（<see cref="StreamChannel"/>）、心跳监视器与关闭状态。
/// 会话的生命周期（接入、握手、加入、拆除）由 <see cref="TcpServer"/> 驱动；公开的 <see cref="ITcpSession"/> 操作委托给通道。
/// 标注"服务端锁"的字段只能在 <see cref="TcpServer"/> 持有其内部锁时读写。
/// </summary>
internal sealed class TcpSession : ITcpSession
{
    private readonly TcpServer owner;
    private readonly Socket socket;
    private readonly ConcurrentDictionary<string, object?> items = new ConcurrentDictionary<string, object?>();
    private readonly CancellationTokenSource closingSource = new CancellationTokenSource();
    private readonly TaskCompletionSource<bool> completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource<bool> connectedDelivered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly object tlsSync = new object();
    private volatile bool connected;
    private volatile bool closing;
    private volatile bool tlsActive;
    private volatile StreamChannel? channel;
    private volatile FrameRouter? router;
    private volatile SessionChannelView? view;
    private bool joined;
    private HeartbeatMonitor? heartbeat;
    private SslStream? tls;
    private bool transportAborted;

    /// <summary>创建会话（尚未建立通道，未加入会话表）。</summary>
    /// <param name="owner">所属服务端。</param>
    /// <param name="id">会话 ID。</param>
    /// <param name="socket">接入的套接字（所有权交给会话）。</param>
    /// <param name="remote">对端端点。</param>
    /// <param name="local">本端端点。</param>
    public TcpSession(TcpServer owner, long id, Socket socket, IPEndPoint remote, IPEndPoint local)
    {
        this.owner = owner;
        this.socket = socket;
        Id = id;
        RemoteEndPoint = remote;
        LocalEndPoint = local;
        Statistics = new ConnectionStatistics();
    }

    /// <inheritdoc />
    public long Id { get; }

    /// <inheritdoc />
    public IPEndPoint RemoteEndPoint { get; }

    /// <inheritdoc />
    public IPEndPoint LocalEndPoint { get; }

    /// <inheritdoc />
    public DateTimeOffset ConnectedAt { get; private set; }

    /// <inheritdoc />
    public bool IsConnected => connected;

    /// <inheritdoc />
    public bool IsTlsActive => tlsActive;

    /// <inheritdoc />
    public ConnectionStatistics Statistics { get; }

    /// <inheritdoc />
    public IDictionary<string, object?> Items => items;

    /// <inheritdoc />
    public event EventHandler<FrameReceivedEventArgs>? FrameReceived;

    /// <inheritdoc />
    public Task<CommResult> SendAsync(ReadOnlyMemory<byte> payload, CancellationToken cancellationToken = default)
    {
        StreamChannel? current = ConnectedChannel();
        if (current == null)
            return Task.FromResult(CommResult.Fail("The session is not connected.", CommErrorKind.NotConnected));

        return current.SendAsync(payload, cancellationToken);
    }

    /// <inheritdoc />
    public Task<CommResult<byte[]>> RequestAsync(ReadOnlyMemory<byte> payload, RequestOptions? options = null, CancellationToken cancellationToken = default)
    {
        StreamChannel? current = ConnectedChannel();
        if (current == null)
            return Task.FromResult(CommResult<byte[]>.Fail("The session is not connected.", CommErrorKind.NotConnected));

        return current.RequestAsync(payload, options, null, cancellationToken);
    }

    /// <inheritdoc />
    public Task<CommResult<byte[]>> ReceiveAsync(RequestOptions? options = null, CancellationToken cancellationToken = default)
    {
        StreamChannel? current = ConnectedChannel();
        if (current == null)
            return Task.FromResult(CommResult<byte[]>.Fail("The session is not connected.", CommErrorKind.NotConnected));

        return current.ReceiveAsync(options, cancellationToken);
    }

    /// <inheritdoc />
    public Task CloseAsync(CancellationToken cancellationToken = default) => owner.CloseSessionAsync(this, cancellationToken);

    // ————— 服务端使用的内部状态 —————

    /// <summary>套接字（由会话拥有）。</summary>
    internal Socket Socket => socket;

    /// <summary>字节流通道；通道创建之前为 null。</summary>
    internal StreamChannel? Channel => channel;

    /// <summary>路由（握手结束时释放积压）；通道创建之前为 null。</summary>
    internal FrameRouter? Router => router;

    /// <summary>握手与心跳使用的字节通道视图（不派发 FrameReceived）。</summary>
    internal SessionChannelView? View => view;

    /// <summary>会话是否已加入会话表（服务端锁）。</summary>
    internal bool Joined => joined;

    /// <summary>会话是否已开始拆除（只读，可无锁读取）。</summary>
    internal bool Closing => closing;

    /// <summary>拆除开始时取消的令牌：握手等待与连接后的排队等待都依赖它被唤醒。</summary>
    internal CancellationToken ClosingToken => closingSource.Token;

    /// <summary>拆除完成的任务：通道与心跳都已停止，且 SessionClosed（如需要）已排队。</summary>
    internal Task Completion => completion.Task;

    /// <summary>SessionConnected 派发完成的信号：完成之后才释放握手积压（服务端锁外完成）。</summary>
    internal TaskCompletionSource<bool> ConnectedDelivered => connectedDelivered;

    /// <summary>心跳监视器（服务端锁）。</summary>
    internal HeartbeatMonitor? Heartbeat
    {
        get => heartbeat;
        set => heartbeat = value;
    }

    /// <summary>绑定路由与通道（服务端锁，在通道启动之前）。</summary>
    internal void Attach(FrameRouter frameRouter, StreamChannel streamChannel)
    {
        router = frameRouter;
        channel = streamChannel;
        view = new SessionChannelView(streamChannel);
    }

    /// <summary>标记为已加入会话表并进入可用状态（服务端锁）。</summary>
    internal void MarkJoined(DateTimeOffset connectedAt)
    {
        joined = true;
        ConnectedAt = connectedAt;
        connected = true;
    }

    /// <summary>开始拆除（服务端锁）：此后会话不可用，且不再加入会话表。</summary>
    internal void BeginClosing()
    {
        connected = false;
        closing = true;
    }

    /// <summary>触发拆除令牌（在服务端锁外调用，以免回调在锁内执行）。</summary>
    internal void CancelClosingToken() => closingSource.Cancel();

    /// <summary>标记拆除完成。</summary>
    internal void CompleteClosing() => completion.TrySetResult(true);

    /// <summary>
    /// 记录 TLS 认证成功的流（服务端锁之外）。传输已被中止时返回 false，调用方负责释放该流。
    /// </summary>
    /// <param name="stream">认证后的流。</param>
    /// <returns>是否已记录。</returns>
    internal bool AttachTls(SslStream stream)
    {
        lock (tlsSync)
        {
            if (transportAborted)
                return false;

            tls = stream;
            tlsActive = true;
            return true;
        }
    }

    /// <summary>
    /// 中止传输：销毁套接字与 TLS 流，使挂起的读写立即失败。可重复调用；之后记录的 TLS 流会被拒绝（<see cref="AttachTls"/>）。
    /// </summary>
    internal void AbortTransport()
    {
        SslStream? secured;
        lock (tlsSync)
        {
            transportAborted = true;
            secured = tls;
            tls = null;
            tlsActive = false;
        }

        socket.Dispose();
        secured?.Dispose();
    }

    /// <summary>优雅关闭的第一步：关闭发送方向（尽力而为），对端据此得知服务端已不再发送。</summary>
    internal void ShutdownSend()
    {
        try
        {
            socket.Shutdown(SocketShutdown.Send);
        }
        catch (SocketException)
        {
            // 对端可能已经断开：优雅关闭只是尽力而为，之后的中止会释放套接字。
        }
        catch (ObjectDisposedException)
        {
            // 套接字已被中止。
        }
    }

    /// <summary>把一帧派发给会话自己的订阅者（逐个隔离异常）。</summary>
    internal void RaiseFrame(FrameReceivedEventArgs args, ILogger logger)
        => FrameRouter.RaiseToSubscribers(FrameReceived, this, args, logger);

    private StreamChannel? ConnectedChannel() => connected ? channel : null;

    /// <summary>
    /// 握手与心跳使用的字节通道视图：绑定到这一条连接，不派发 <c>FrameReceived</c>（握手期间未认领的帧进入积压，握手后才派发）。
    /// </summary>
    internal sealed class SessionChannelView : IByteChannel
    {
        private readonly StreamChannel channel;

        /// <summary>创建视图。</summary>
        /// <param name="channel">会话的字节通道。</param>
        public SessionChannelView(StreamChannel channel)
        {
            this.channel = channel;
        }

        /// <inheritdoc />
        public Task<CommResult> SendAsync(ReadOnlyMemory<byte> payload, CancellationToken cancellationToken = default)
            => channel.SendAsync(payload, cancellationToken);

        /// <inheritdoc />
        public Task<CommResult<byte[]>> RequestAsync(ReadOnlyMemory<byte> payload, RequestOptions? options = null,
                                                     CancellationToken cancellationToken = default)
            => channel.RequestAsync(payload, options, null, cancellationToken);

        /// <inheritdoc />
        public Task<CommResult<byte[]>> ReceiveAsync(RequestOptions? options = null, CancellationToken cancellationToken = default)
            => channel.ReceiveAsync(options, cancellationToken);

        /// <summary>握手与心跳期间不派发事件：订阅被忽略。</summary>
        public event EventHandler<FrameReceivedEventArgs>? FrameReceived
        {
            add
            {
            }

            remove
            {
            }
        }
    }
}
