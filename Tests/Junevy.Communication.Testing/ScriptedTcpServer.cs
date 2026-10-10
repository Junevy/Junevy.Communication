using System.Net;
using System.Net.Sockets;

namespace Junevy.Communication.Testing;

/// <summary>
/// 测试辅助 TCP 服务端：接受连接后按调用方提供的脚本处理每个连接，不等待脚本完成。
/// 脚本参数为连接序号（从 0 开始）、该连接的流（已统计读取字节数）、服务端关闭令牌。
/// 用于验证客户端在不同服务端应答序列下的行为（重连、连接销毁、终态不重试等）。
/// 由 Modbus 测试工程的同名类复制并泛化（D15）。
/// </summary>
public sealed class ScriptedTcpServer : IDisposable
{
    private readonly TcpListener listener;
    private readonly Func<int, Stream, CancellationToken, Task> handler;
    private readonly List<TcpClient> connections = new List<TcpClient>();
    private readonly object connectionsLock = new object();
    private readonly CancellationTokenSource shutdown = new CancellationTokenSource();
    private int acceptedConnectionCount;
    private int receivedBytes;
    private int closedByPeerCount;
    private volatile bool disposed;

    private ScriptedTcpServer(TcpListener listener, int port, Func<int, Stream, CancellationToken, Task> handler)
    {
        this.listener = listener;
        this.handler = handler;
        Port = port;
    }

    /// <summary>服务端监听的端口（回环地址，随机分配）。</summary>
    public int Port { get; }

    /// <summary>已接受的连接数。</summary>
    public int AcceptedConnectionCount => Volatile.Read(ref acceptedConnectionCount);

    /// <summary>脚本经由连接流读取到的累计字节数。</summary>
    public int ReceivedBytes => Volatile.Read(ref receivedBytes);

    /// <summary>读到 0 字节（对端关闭连接）的连接数；每个连接至多计一次。</summary>
    public int ClosedByPeerCount => Volatile.Read(ref closedByPeerCount);

    /// <summary>
    /// 启动服务端并开始接受连接。
    /// </summary>
    /// <param name="handler">连接处理脚本：参数为连接序号、该连接的流、服务端关闭令牌。</param>
    /// <returns>已启动的服务端；调用方负责 <see cref="Dispose"/>。</returns>
    /// <exception cref="ArgumentNullException"><paramref name="handler"/> 为 null。</exception>
    public static ScriptedTcpServer Start(Func<int, Stream, CancellationToken, Task> handler)
    {
        if (handler == null)
            throw new ArgumentNullException(nameof(handler));

        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var server = new ScriptedTcpServer(listener, PortOf(listener), handler);
        server.AcceptLoop();
        return server;
    }

    /// <summary>
    /// 主动断开第 <paramref name="index"/> 个连接（按接受顺序，从 0 开始）。用于重连测试。
    /// </summary>
    /// <param name="index">连接序号。</param>
    /// <exception cref="ArgumentOutOfRangeException">该序号的连接尚未被接受。</exception>
    public void CloseConnection(int index)
    {
        TcpClient client;
        lock (connectionsLock)
        {
            if (index < 0 || index >= connections.Count)
                throw new ArgumentOutOfRangeException(nameof(index), index, "No connection with this index has been accepted.");

            client = connections[index];
        }

        client.Close();
    }

    /// <summary>
    /// 从流中读满 <paramref name="count"/> 字节。连接提前结束时返回 false。
    /// </summary>
    /// <param name="stream">连接流。</param>
    /// <param name="buffer">目标缓冲，长度不小于 <paramref name="count"/>。</param>
    /// <param name="count">需要读取的字节数。</param>
    /// <returns>读满返回 true；连接提前结束返回 false。</returns>
    public static async Task<bool> ReadExactAsync(Stream stream, byte[] buffer, int count)
    {
        int total = 0;
        while (total < count)
        {
            int read = await stream.ReadAsync(buffer, total, count - total).ConfigureAwait(false);
            if (read == 0)
                return false;

            total += read;
        }

        return true;
    }

    /// <summary>停止监听并关闭全部连接。可重复调用。</summary>
    public void Dispose()
    {
        if (disposed)
            return;
        disposed = true;

        try
        {
            shutdown.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // 已释放：无需再次取消。
        }

        try
        {
            listener.Stop();
        }
        catch (SocketException)
        {
            // 监听器已停止。
        }

        lock (connectionsLock)
        {
            foreach (TcpClient connection in connections)
                connection.Dispose();

            connections.Clear();
        }

        shutdown.Dispose();
    }

    private static int PortOf(TcpListener listener)
    {
        var endPoint = listener.LocalEndpoint as IPEndPoint
            ?? throw new InvalidOperationException("The listener is not bound to an IP endpoint.");
        return endPoint.Port;
    }

    private void AcceptLoop()
    {
        _ = Task.Run(async () =>
        {
            while (!disposed)
            {
                TcpClient client;
                try
                {
                    client = await listener.AcceptTcpClientAsync().ConfigureAwait(false);
                }
                catch (Exception)
                {
                    // Dispose 之后 listener.Stop() 使挂起的 Accept 抛出异常，正常退出。
                    break;
                }

                int connectionIndex;
                lock (connectionsLock)
                {
                    connectionIndex = connections.Count;
                    connections.Add(client);
                }

                Interlocked.Increment(ref acceptedConnectionCount);

                // 不等待脚本：脚本自身负责读写与结束。
                _ = RunHandlerAsync(client, connectionIndex);
            }
        });
    }

    private async Task RunHandlerAsync(TcpClient client, int connectionIndex)
    {
        try
        {
            Stream stream = new CountingStream(client.GetStream(), this);
            await handler(connectionIndex, stream, shutdown.Token).ConfigureAwait(false);
        }
        catch (Exception)
        {
            // 脚本异常，或 Dispose / CloseConnection 引发的 IO 异常：服务端不因单个连接失败而终止。
        }
    }

    /// <summary>
    /// 统计读取字节数与对端关闭次数的流包装。写入与其他操作直接转发。
    /// </summary>
    private sealed class CountingStream : Stream
    {
        private readonly Stream inner;
        private readonly ScriptedTcpServer owner;
        private int eofReported;

        public CountingStream(Stream inner, ScriptedTcpServer owner)
        {
            this.inner = inner;
            this.owner = owner;
        }

        public override bool CanRead => inner.CanRead;

        public override bool CanSeek => false;

        public override bool CanWrite => inner.CanWrite;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override void Flush() => inner.Flush();

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override int Read(byte[] buffer, int offset, int count)
            => Record(inner.Read(buffer, offset, count), count);

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
            => ReadAndRecordAsync(buffer, offset, count, cancellationToken);

        public override void Write(byte[] buffer, int offset, int count) => inner.Write(buffer, offset, count);

        public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
            => inner.WriteAsync(buffer, offset, count, cancellationToken);

        private async Task<int> ReadAndRecordAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        {
            int read = await inner.ReadAsync(buffer, offset, count, cancellationToken).ConfigureAwait(false);
            return Record(read, count);
        }

        private int Record(int read, int requested)
        {
            // 零长度读取不代表对端关闭。
            if (requested > 0)
            {
                if (read > 0)
                    Interlocked.Add(ref owner.receivedBytes, read);
                else if (Interlocked.Exchange(ref eofReported, 1) == 0)
                    Interlocked.Increment(ref owner.closedByPeerCount);
            }

            return read;
        }
    }
}
