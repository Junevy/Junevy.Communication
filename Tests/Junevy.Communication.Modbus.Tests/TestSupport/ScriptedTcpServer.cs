using System.Net;
using System.Net.Sockets;

namespace Junevy.Communication.Modbus.Tests.TestSupport
{
    /// <summary>
    /// 测试辅助 TCP 服务端：接受连接后按调用方提供的脚本处理每个连接
    /// （参数为连接序号、从 0 开始；该连接的流；服务端关闭令牌），不等待脚本完成。
    /// 用于验证客户端在不同服务端应答序列下的行为（重连、连接销毁、终态不重试等）。
    /// </summary>
    internal sealed class ScriptedTcpServer : IDisposable
    {
        private readonly TcpListener listener;
        private readonly Func<int, NetworkStream, CancellationToken, Task> handler;
        private readonly List<TcpClient> connections = new();
        private readonly object connectionsLock = new();
        private readonly CancellationTokenSource shutdown = new();
        private int acceptedConnectionCount;
        private volatile bool disposed;

        private ScriptedTcpServer(TcpListener listener, int port, Func<int, NetworkStream, CancellationToken, Task> handler)
        {
            this.listener = listener;
            this.handler = handler;
            Port = port;
        }

        public int Port { get; }

        public int AcceptedConnectionCount => Volatile.Read(ref acceptedConnectionCount);

        public static ScriptedTcpServer Start(Func<int, NetworkStream, CancellationToken, Task> handler)
        {
            if (handler == null)
                throw new ArgumentNullException(nameof(handler));

            var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            var port = ((IPEndPoint)listener.LocalEndpoint).Port;
            var server = new ScriptedTcpServer(listener, port, handler);
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
                    int connectionIndex;
                    try
                    {
                        client = await listener.AcceptTcpClientAsync();
                    }
                    catch
                    {
                        // disposed 后 Stop() 使挂起的 Accept 抛异常，正常退出
                        break;
                    }

                    connectionIndex = Interlocked.Increment(ref acceptedConnectionCount) - 1;
                    lock (connectionsLock)
                        connections.Add(client);

                    // 不等待 handler：脚本自身负责读写与结束
                    _ = RunHandlerAsync(client, connectionIndex);
                }
            });
        }

        private async Task RunHandlerAsync(TcpClient client, int connectionIndex)
        {
            try
            {
                var stream = client.GetStream();
                await handler(connectionIndex, stream, shutdown.Token);
            }
            catch
            {
                // 脚本异常或 Dispose 引发的 IO 异常；服务端不因单个连接失败而终止
            }
        }

        public void Dispose()
        {
            if (disposed)
                return;
            disposed = true;

            try
            {
                shutdown.Cancel();
            }
            catch
            {
            }

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

            shutdown.Dispose();
        }

        // ————————————————— 脚本辅助 —————————————————

        /// <summary>从流中读满 <paramref name="count"/> 字节；连接提前结束返回 false。</summary>
        internal static async Task<bool> ReadExactAsync(NetworkStream stream, byte[] buffer, int count)
        {
            int total = 0;
            while (total < count)
            {
                int read = await stream.ReadAsync(buffer, total, count - total);
                if (read == 0)
                    return false;

                total += read;
            }

            return true;
        }

        /// <summary>构造"读 1 个保持寄存器"的合法响应帧，回显请求的事务 ID。</summary>
        internal static byte[] ReadHoldingRegistersResponse(ReadOnlySpan<byte> request, ushort value = 1)
            => new byte[]
            {
                request[0], request[1],
                0x00, 0x00,
                0x00, 0x05,
                request[6],
                0x03,
                0x02,
                (byte)(value >> 8), (byte)value
            };
    }
}