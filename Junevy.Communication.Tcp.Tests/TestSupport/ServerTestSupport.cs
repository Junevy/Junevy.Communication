using System.Net;
using System.Net.Sockets;
using Junevy.Communication.Channels;
using Junevy.Communication.Core.Results;
using static Junevy.Communication.Tcp.Tests.TcpTestHelpers;

namespace Junevy.Communication.Tcp.Tests;

/// <summary>以委托实现的握手钩子（测试用）。</summary>
internal sealed class DelegateInitializer : IConnectionInitializer
{
    private readonly Func<IByteChannel, CancellationToken, Task<CommResult>> run;

    public DelegateInitializer(Func<IByteChannel, CancellationToken, Task<CommResult>> run)
    {
        this.run = run;
    }

    public Task<CommResult> InitializeAsync(IByteChannel channel, CancellationToken cancellationToken) => run(channel, cancellationToken);
}

/// <summary>以委托实现的连接过滤器（测试用）。</summary>
internal sealed class DelegateFilter : IConnectionFilter
{
    private readonly Func<IPEndPoint, bool> accept;

    public DelegateFilter(Func<IPEndPoint, bool> accept)
    {
        this.accept = accept;
    }

    public bool Accept(IPEndPoint remote) => accept(remote);
}

/// <summary>
/// TCP 服务端测试的共享辅助：回环监听配置、按行分帧的客户端通道、原始 TCP 连接与对端关闭判定。
/// </summary>
internal static class ServerTestHelpers
{
    /// <summary>回环监听配置（按行分帧）；端口由调用方给出。</summary>
    public static TcpServerConfig CreateServerConfig(int port)
        => new TcpServerConfig { ListenAddress = "127.0.0.1", Port = port, Framing = LineFraming() };

    /// <summary>连接到回环服务端的客户端通道（按行分帧，连接超时 10000 毫秒）。<paramref name="localAddress"/> 为 null 时由系统选择。</summary>
    public static TcpClientChannel CreateClient(int port, string? localAddress = null)
        => new TcpClientChannel(new TcpClientChannelConfig
        {
            Host = "127.0.0.1",
            Port = port,
            ConnectTimeout = 10000,
            Framing = LineFraming(),
            LocalAddress = localAddress,
        });

    /// <summary>原始 TCP 连接（不做分帧），用于观察服务端是否直接关闭了连接。</summary>
    public static async Task<TcpClient> ConnectRawAsync(int port, string? localAddress = null)
    {
        var client = new TcpClient(AddressFamily.InterNetwork);
        if (localAddress != null)
            client.Client.Bind(new IPEndPoint(IPAddress.Parse(localAddress), 0));

        await client.ConnectAsync(IPAddress.Loopback, port).ConfigureAwait(false);
        return client;
    }

    /// <summary>
    /// 等待服务端关闭连接：读取返回 0 或因复位而抛出 I/O 异常均视为已关闭。在 <paramref name="milliseconds"/> 内未关闭返回 false。
    /// </summary>
    public static async Task<bool> WaitForPeerCloseAsync(TcpClient client, int milliseconds)
    {
        var buffer = new byte[16];
        Task<int> read = ReadOrClosedAsync(client, buffer);
        Task finished = await Task.WhenAny(read, Task.Delay(milliseconds)).ConfigureAwait(false);
        if (!ReferenceEquals(finished, read))
            return false;

        return await read.ConfigureAwait(false) == 0;
    }

    // 复位导致的 I/O 异常与正常 EOF 同样表示连接已被对端关闭，统一返回 0。
    private static async Task<int> ReadOrClosedAsync(TcpClient client, byte[] buffer)
    {
        try
        {
            return await client.GetStream().ReadAsync(buffer, 0, buffer.Length).ConfigureAwait(false);
        }
        catch (IOException)
        {
            return 0;
        }
    }
}

/// <summary>
/// 按派发顺序记录服务端事件的测试记录器（线程安全）。订阅时同时为每个新会话订阅其 <c>FrameReceived</c>，
/// 因此会话级帧事件与服务端帧事件都进入同一条顺序记录。
/// </summary>
internal sealed class ServerRecorder
{
    private readonly object sync = new object();
    private readonly List<ITcpSession> connected = new List<ITcpSession>();
    private readonly List<long> connectedTimestamps = new List<long>();
    private readonly List<ClosedRecord> closed = new List<ClosedRecord>();
    private readonly List<ServerState> states = new List<ServerState>();
    private readonly List<string> frames = new List<string>();

    /// <summary>订阅服务端的全部事件。</summary>
    /// <param name="server">要记录的服务端。</param>
    public ServerRecorder(TcpServer server)
    {
        server.StateChanged += (sender, args) =>
        {
            lock (sync)
                states.Add(args.CurrentState);
        };

        server.SessionConnected += (sender, args) =>
        {
            lock (sync)
            {
                connected.Add(args.Session);
                connectedTimestamps.Add(System.Diagnostics.Stopwatch.GetTimestamp());
            }

            args.Session.FrameReceived += (session, frame) => Record("session:" + Text(frame.Data));
        };

        server.SessionClosed += (sender, args) =>
        {
            lock (sync)
                closed.Add(new ClosedRecord(args.Session, args.Reason, args.Exception, System.Diagnostics.Stopwatch.GetTimestamp()));
        };

        server.FrameReceived += (sender, args) => Record("server:" + Text(args.Data));
    }

    /// <summary>已派发 SessionConnected 的次数。</summary>
    public int ConnectedCount
    {
        get
        {
            lock (sync)
                return connected.Count;
        }
    }

    /// <summary>已派发 SessionClosed 的次数。</summary>
    public int ClosedCount
    {
        get
        {
            lock (sync)
                return closed.Count;
        }
    }

    /// <summary>SessionConnected 的会话（快照，按派发顺序）。</summary>
    public IReadOnlyList<ITcpSession> Connected
    {
        get
        {
            lock (sync)
                return connected.ToList();
        }
    }

    /// <summary>SessionConnected 派发时刻的 <see cref="System.Diagnostics.Stopwatch"/> 时间戳（快照）。</summary>
    public IReadOnlyList<long> ConnectedTimestamps
    {
        get
        {
            lock (sync)
                return connectedTimestamps.ToList();
        }
    }

    /// <summary>SessionClosed 记录（快照，按派发顺序）。</summary>
    public IReadOnlyList<ClosedRecord> Closed
    {
        get
        {
            lock (sync)
                return closed.ToList();
        }
    }

    /// <summary>StateChanged 的目标状态（快照，按派发顺序）。</summary>
    public IReadOnlyList<ServerState> States
    {
        get
        {
            lock (sync)
                return states.ToList();
        }
    }

    /// <summary>帧记录（快照，按派发顺序）：<c>session:&lt;文本&gt;</c> 或 <c>server:&lt;文本&gt;</c>。</summary>
    public IReadOnlyList<string> Frames
    {
        get
        {
            lock (sync)
                return frames.ToList();
        }
    }

    private void Record(string entry)
    {
        lock (sync)
            frames.Add(entry);
    }

    private static string Text(byte[] data) => System.Text.Encoding.ASCII.GetString(data);

    /// <summary>一条 SessionClosed 记录。</summary>
    public sealed class ClosedRecord
    {
        /// <summary>创建记录。</summary>
        public ClosedRecord(ITcpSession session, DisconnectReason reason, Exception? exception, long timestamp)
        {
            Session = session;
            Reason = reason;
            Exception = exception;
            Timestamp = timestamp;
        }

        /// <summary>已关闭的会话。</summary>
        public ITcpSession Session { get; }

        /// <summary>关闭原因。</summary>
        public DisconnectReason Reason { get; }

        /// <summary>相关异常，可为 null。</summary>
        public Exception? Exception { get; }

        /// <summary>派发时刻的 <see cref="System.Diagnostics.Stopwatch"/> 时间戳。</summary>
        public long Timestamp { get; }
    }
}
