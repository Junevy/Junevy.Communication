using System.Net;
using System.Net.Sockets;

namespace Junevy.Communication.Modbus.Tests.TestSupport
{
    /// <summary>
    /// 测试辅助 TCP 服务端：接受连接、读取并累计收到的字节数，从不回复。
    /// 用于验证客户端在服务端不应答时的超时行为。
    /// </summary>
    internal sealed class SilentTcpServer : IDisposable
    {
        private readonly TcpListener listener;
        private readonly List<TcpClient> connections = new();
        private readonly object connectionsLock = new();
        private int acceptedConnectionCount;
        private int receivedBytes;
        private int closedByPeerCount;
        private volatile bool disposed;

        private SilentTcpServer(TcpListener listener, int port)
        {
            this.listener = listener;
            Port = port;
        }

        public int Port { get; }

        public int AcceptedConnectionCount => Volatile.Read(ref acceptedConnectionCount);

        public int ReceivedBytes => Volatile.Read(ref receivedBytes);

        /// <summary>读到 0 字节（对端关闭连接）而结束的连接数。</summary>
        public int ClosedByPeerCount => Volatile.Read(ref closedByPeerCount);

        /// <summary>连接建立后同步回调（在读取循环启动前调用），默认 null。</summary>
        public Action<NetworkStream>? OnConnected { get; set; }

        public static SilentTcpServer Start()
        {
            var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            var port = ((IPEndPoint)listener.LocalEndpoint).Port;
            var server = new SilentTcpServer(listener, port);
            server.AcceptLoop();
            return server;
        }

        private void AcceptLoop()
        {
            Task.Run(async () =>
            {
                while (!disposed)
                {
                    TcpClient client;
                    try
                    {
                        client = await listener.AcceptTcpClientAsync();
                    }
                    catch
                    {
                        // disposed 后 Stop() 使挂起的 Accept 抛异常，正常退出
                        break;
                    }

                    Interlocked.Increment(ref acceptedConnectionCount);
                    lock (connectionsLock)
                        connections.Add(client);

                    try
                    {
                        OnConnected?.Invoke(client.GetStream());
                    }
                    catch
                    {
                        // 回调异常不终止服务端
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
                    var stream = client.GetStream();
                    while (!disposed)
                    {
                        int read = await stream.ReadAsync(buffer);
                        if (read == 0)
                        {
                            Interlocked.Increment(ref closedByPeerCount);
                            break;
                        }
                        Interlocked.Add(ref receivedBytes, read);
                    }
                }
            }
            catch
            {
                // Dispose 引发的 IO/ObjectDisposed 异常，正常退出
            }
        }

        public void Dispose()
        {
            if (disposed)
                return;
            disposed = true;

            try
            {
                listener.Stop();
            }
            catch
            {
            }

            lock (connectionsLock)
            {
                foreach (var connection in connections)
                    connection.Dispose();
                connections.Clear();
            }
        }
    }
}
