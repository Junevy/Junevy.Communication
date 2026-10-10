using System.Net;
using System.Net.Sockets;

namespace Junevy.Communication.Testing;

/// <summary>
/// 测试辅助 TCP 服务端：接受连接、读取并累计收到的字节数，从不回复。
/// 用于验证客户端在服务端不应答时的超时行为。由 Modbus 测试工程的同名类复制并补充（D15）。
/// </summary>
public sealed class SilentTcpServer : IDisposable
{
    private readonly TcpListener listener;
    private readonly List<TcpClient> connections = new List<TcpClient>();
    private readonly object connectionsLock = new object();
    private int acceptedConnectionCount;
    private int receivedBytes;
    private int closedByPeerCount;
    private volatile bool disposed;

    private SilentTcpServer(TcpListener listener, int port)
    {
        this.listener = listener;
        Port = port;
    }

    /// <summary>服务端监听的端口（回环地址，随机分配）。</summary>
    public int Port { get; }

    /// <summary>已接受的连接数。</summary>
    public int AcceptedConnectionCount => Volatile.Read(ref acceptedConnectionCount);

    /// <summary>从连接读取到的累计字节数。</summary>
    public int ReceivedBytes => Volatile.Read(ref receivedBytes);

    /// <summary>读到 0 字节（对端关闭连接）而结束的连接数。</summary>
    public int ClosedByPeerCount => Volatile.Read(ref closedByPeerCount);

    /// <summary>连接建立后的同步回调（在读取循环启动前调用），默认 null。</summary>
    public Action<NetworkStream>? OnConnected { get; set; }

    /// <summary>
    /// 启动服务端并开始接受连接。
    /// </summary>
    /// <returns>已启动的服务端；调用方负责 <see cref="Dispose"/>。</returns>
    public static SilentTcpServer Start()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var endPoint = listener.LocalEndpoint as IPEndPoint
            ?? throw new InvalidOperationException("The listener is not bound to an IP endpoint.");

        var server = new SilentTcpServer(listener, endPoint.Port);
        server.AcceptLoop();
        return server;
    }

    /// <summary>停止监听并关闭全部连接。可重复调用。</summary>
    public void Dispose()
    {
        if (disposed)
            return;
        disposed = true;

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

                Interlocked.Increment(ref acceptedConnectionCount);
                lock (connectionsLock)
                    connections.Add(client);

                try
                {
                    OnConnected?.Invoke(client.GetStream());
                }
                catch (Exception)
                {
                    // 回调异常不终止服务端。
                }

                _ = ReadLoopAsync(client);
            }
        });
    }

    private async Task ReadLoopAsync(TcpClient client)
    {
        var buffer = new byte[4096];
        try
        {
            using (client)
            {
                NetworkStream stream = client.GetStream();
                while (!disposed)
                {
                    int read = await stream.ReadAsync(buffer, 0, buffer.Length).ConfigureAwait(false);
                    if (read == 0)
                    {
                        Interlocked.Increment(ref closedByPeerCount);
                        break;
                    }

                    Interlocked.Add(ref receivedBytes, read);
                }
            }
        }
        catch (Exception)
        {
            // Dispose 引发的 IO / ObjectDisposed 异常，正常退出。
        }
    }
}
