using System.Net;
using System.Net.Sockets;

namespace Junevy.Communication.Tcp.Tests;

/// <summary>
/// 测试用回环服务端：可设置监听套接字的接收缓冲区（被接受的连接继承该设置），并把每个 <see cref="TcpClient"/> 交给处理器，
/// 因此处理器可以读取对端的远端端口。处理器返回后连接保持打开，直到服务端释放。
/// </summary>
internal sealed class LoopbackServer : IDisposable
{
    private readonly TcpListener listener;
    private readonly Func<int, TcpClient, CancellationToken, Task> handler;
    private readonly CancellationTokenSource shutdown = new CancellationTokenSource();
    private readonly List<TcpClient> clients = new List<TcpClient>();
    private readonly object sync = new object();
    private int acceptedCount;
    private volatile bool disposed;

    private LoopbackServer(TcpListener listener, Func<int, TcpClient, CancellationToken, Task> handler)
    {
        this.listener = listener;
        this.handler = handler;
        Port = ((IPEndPoint)listener.LocalEndpoint).Port;
    }

    /// <summary>监听端口（回环地址，随机分配）。</summary>
    public int Port { get; }

    /// <summary>已接受的连接数。</summary>
    public int AcceptedConnectionCount => Volatile.Read(ref acceptedCount);

    /// <summary>启动服务端。</summary>
    /// <param name="handler">连接处理器：参数为连接序号、客户端对象与服务端关闭令牌。</param>
    /// <param name="receiveBufferSize">监听套接字的接收缓冲区（字节）；0 表示系统默认。</param>
    public static LoopbackServer Start(Func<int, TcpClient, CancellationToken, Task> handler, int receiveBufferSize = 0)
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        if (receiveBufferSize > 0)
            listener.Server.ReceiveBufferSize = receiveBufferSize;

        listener.Start();
        var server = new LoopbackServer(listener, handler);
        _ = server.AcceptLoopAsync();
        return server;
    }

    /// <summary>停止监听并释放全部连接。可重复调用。</summary>
    public void Dispose()
    {
        if (disposed)
            return;
        disposed = true;

        shutdown.Cancel();
        listener.Stop();
        lock (sync)
        {
            foreach (TcpClient client in clients)
                client.Dispose();

            clients.Clear();
        }
    }

    private async Task AcceptLoopAsync()
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

            int index = Interlocked.Increment(ref acceptedCount) - 1;
            lock (sync)
                clients.Add(client);

            _ = RunHandlerAsync(index, client);
        }
    }

    private async Task RunHandlerAsync(int index, TcpClient client)
    {
        try
        {
            await handler(index, client, shutdown.Token).ConfigureAwait(false);
        }
        catch (Exception)
        {
            // 处理器异常只结束本连接。
        }
    }
}
