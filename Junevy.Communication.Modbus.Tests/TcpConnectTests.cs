using Junevy.Communication.Modbus.Tcp;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;

namespace Junevy.Communication.Modbus.Tests
{
    /// <summary>
    /// TCP 连接行为验证：ConnectAsync 必须是真异步（不占用调用线程到 ConnectTimeout），
    /// 连接失败/超时时返回 false 且不抛异常。
    /// </summary>
[Collection(SocketTimingCollection.Name)]
    public class TcpConnectTests
    {
        [Fact]
        public async Task ConnectAsync_ListeningPort_ReturnsTrue()
        {
            using var listener = StartListener();
            int port = ((IPEndPoint)listener.LocalEndpoint).Port;
            using var tcp = CreateClient(port, connectTimeout: 10000);

            Assert.True(await tcp.ConnectAsync());
            Assert.True(tcp.IsConnected);
        }

        [Fact]
        public async Task ConnectAsync_ClosedPort_ReturnsFalse()
        {
            int port = StartAndStopListener();
            using var tcp = CreateClient(port, connectTimeout: 10000);

            var sw = Stopwatch.StartNew();
            bool connected = await tcp.ConnectAsync();
            sw.Stop();

            Assert.False(connected);
            Assert.False(tcp.IsConnected);
            Assert.True(sw.ElapsedMilliseconds < 5000, $"ConnectAsync took {sw.ElapsedMilliseconds}ms");
        }

        [Fact]
        public void Connect_ClosedPort_ReturnsFalse()
        {
            int port = StartAndStopListener();
            using var tcp = CreateClient(port, connectTimeout: 10000);

            Assert.False(tcp.Connect());
            Assert.False(tcp.IsConnected);
        }

        [Fact]
        [Trait("Category", "Network")]
        public async Task ConnectAsync_UnroutableAddress_ShortTimeout_ReturnsFalse()
        {
            using var tcp = CreateClient(unroutableAddress: true, connectTimeout: 1);

            var connectTask = tcp.ConnectAsync();
            var completed = await Task.WhenAny(connectTask, Task.Delay(30000));

            Assert.True(completed == connectTask, "ConnectAsync did not complete within 30s");
            bool connected = await connectTask;   // 不可达只返回 false，不抛异常
            Assert.False(connected);
            Assert.False(tcp.IsConnected);
        }

        [Fact]
        [Trait("Category", "Network")]
        public void Connect_UnroutableAddress_RespectsConnectTimeout()
        {
            using var tcp = CreateClient(unroutableAddress: true, connectTimeout: 500);

            var sw = Stopwatch.StartNew();
            bool connected = tcp.Connect();
            sw.Stop();

            Assert.False(connected);
            Assert.InRange(sw.ElapsedMilliseconds, 450, 2500);
        }

        [Fact]
        public async Task ConnectAsync_EmptyAddress_ReturnsFalse()
        {
            using var tcp = new ModbusTcpClient(new ModbusTcpClientConfig
            {
                Address = string.Empty,
                Port = 502,
                ConnectTimeout = 10000
            });

            Assert.False(await tcp.ConnectAsync());
        }

        // ————————————————— 私有辅助 —————————————————

        private static TcpListener StartListener()
        {
            var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            return listener;
        }

        private static int StartAndStopListener()
        {
            var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            int port = ((IPEndPoint)listener.LocalEndpoint).Port;
            listener.Stop();
            return port;
        }

        private static ModbusTcpClient CreateClient(int port, int connectTimeout)
            => new(new ModbusTcpClientConfig
            {
                Address = "127.0.0.1",
                Port = port,
                ConnectTimeout = connectTimeout,
                ReadTimeout = 2000,
                WriteTimeout = 2000,
                RetryCount = 0,
                Reconnect = false
            });

        /// <summary>构造一个指向不可路由地址的客户端（RFC 1918 段中通常不可路由的地址）。</summary>
        private static ModbusTcpClient CreateClient(bool unroutableAddress, int connectTimeout)
            => new(new ModbusTcpClientConfig
            {
                Address = unroutableAddress ? "10.255.255.1" : "127.0.0.1",
                Port = 502,
                ConnectTimeout = connectTimeout,
                ReadTimeout = 2000,
                WriteTimeout = 2000,
                RetryCount = 0,
                Reconnect = false
            });
    }
}