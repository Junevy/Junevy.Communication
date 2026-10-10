using Junevy.Communication.Modbus.Core.Models;
using Junevy.Communication.Modbus.Tcp;
using Junevy.Communication.Modbus.Tests.TestSupport;
using System.Diagnostics;

namespace Junevy.Communication.Modbus.Tests
{
    /// <summary>
    /// Dispose 与在途/排队请求的互斥契约：
    /// Dispose 前已进入的请求返回 ConnectionClosed 而非抛异常；Dispose 之后的新调用抛
    /// ObjectDisposedException；Disconnect 变为空操作；用户自己的取消仍归 Cancelled。
    /// </summary>
[Collection(SocketTimingCollection.Name)]
    public class DisposeConcurrencyTests
    {
        [Fact]
        public void Dispose_DuringSyncRequest_ReturnsConnectionClosed()
        {
            for (int iteration = 0; iteration < 20; iteration++)
            {
                using var server = SilentTcpServer.Start();
                using var tcp = CreateClient(server.Port);

                Assert.True(ConnectWithRetry(tcp), $"iteration {iteration}: tcp.Connect() failed");
                var requestTask = Task.Run(() => tcp.Request(CreateReadHoldingRegistersRequest()));

                // 等服务端确实收到完整请求帧，确保请求已进入传输层（正在等响应）再 Dispose。
                // 否则 Task.Run 体可能尚未开始，命中的是"Dispose 之后才调用 Request"契约（抛
                // ObjectDisposedException），而不是本用例要验证的"在途请求"。
                Assert.True(WaitForReceivedBytes(server, RequestLength, 10000),
                    $"iteration {iteration}: request never reached the server");

                tcp.Dispose();

                Assert.True(requestTask.Wait(3000), $"iteration {iteration}: sync request did not complete within 3000ms");
                var result = requestTask.Result;
                Assert.False(result.IsSuccess, $"iteration {iteration}");
                Assert.Equal(ModbusErrorKind.ConnectionClosed, result.ErrorKind);
            }
        }

        [Fact]
        public async Task Dispose_DuringAsyncRequest_ReturnsConnectionClosed()
        {
            for (int iteration = 0; iteration < 20; iteration++)
            {
                using var server = SilentTcpServer.Start();
                using var tcp = CreateClient(server.Port);

                Assert.True(ConnectWithRetry(tcp));
                var requestTask = tcp.RequestAsync(CreateReadHoldingRegistersRequest());

                Assert.True(await WaitForReceivedBytesAsync(server, RequestLength, 10000),
                    $"iteration {iteration}: request never reached the server");

                tcp.Dispose();

                var completed = await Task.WhenAny(requestTask, Task.Delay(3000));
                Assert.True(completed == requestTask, $"iteration {iteration}: async request did not complete within 3000ms");
                var result = await requestTask;
                Assert.False(result.IsSuccess, $"iteration {iteration}");
                Assert.Equal(ModbusErrorKind.ConnectionClosed, result.ErrorKind);
            }
        }

        [Fact]
        public async Task Dispose_WithQueuedRequests_AllReturnConnectionClosed()
        {
            using var server = SilentTcpServer.Start();
            using var tcp = CreateClient(server.Port);

            Assert.True(ConnectWithRetry(tcp));
            var inFlight = tcp.RequestAsync(CreateReadHoldingRegistersRequest());
            Assert.True(await WaitForReceivedBytesAsync(server, RequestLength, 10000),
                "in-flight request never reached the server");

            var queued = new[]
            {
                tcp.RequestAsync(CreateReadHoldingRegistersRequest()),
                tcp.RequestAsync(CreateReadHoldingRegistersRequest()),
                tcp.RequestAsync(CreateReadHoldingRegistersRequest())
            };

            tcp.Dispose();

            var allTasks = new[] { inFlight }.Concat(queued).ToArray();
            var all = Task.WhenAll(allTasks);
            var completed = await Task.WhenAny(all, Task.Delay(3000));
            Assert.True(completed == all, "queued requests did not all complete within 3000ms");

            foreach (var result in await all)
            {
                Assert.False(result.IsSuccess);
                Assert.Equal(ModbusErrorKind.ConnectionClosed, result.ErrorKind);
            }
        }

        [Fact]
        public void Request_AfterDispose_ThrowsObjectDisposed()
        {
            using var server = SilentTcpServer.Start();
            using var tcp = CreateClient(server.Port);
            tcp.Dispose();

            Assert.Throws<ObjectDisposedException>(() => tcp.Request(CreateReadHoldingRegistersRequest()));
        }

        [Fact]
        public async Task RequestAsync_AfterDispose_ThrowsObjectDisposed()
        {
            using var server = SilentTcpServer.Start();
            using var tcp = CreateClient(server.Port);
            tcp.Dispose();

            await Assert.ThrowsAsync<ObjectDisposedException>(
                () => tcp.RequestAsync(CreateReadHoldingRegistersRequest()));
        }

        [Fact]
        public async Task Connect_AfterDispose_ThrowsObjectDisposed()
        {
            using var server = SilentTcpServer.Start();
            using var tcp = CreateClient(server.Port);
            tcp.Dispose();

            Assert.Throws<ObjectDisposedException>(() => tcp.Connect());
            await Assert.ThrowsAsync<ObjectDisposedException>(() => tcp.ConnectAsync());
        }

        [Fact]
        public void Dispose_Twice_DoesNotThrow()
        {
            using var server = SilentTcpServer.Start();
            using var tcp = CreateClient(server.Port);
            Assert.True(ConnectWithRetry(tcp));

            tcp.Dispose();
            tcp.Dispose();   // 顺序调用两次

            using var tcp2 = CreateClient(server.Port);
            Assert.True(ConnectWithRetry(tcp2), "second client failed to connect");
            var gate = new ManualResetEventSlim(false);
            var t1 = Task.Run(() => { gate.Wait(); tcp2.Dispose(); });
            var t2 = Task.Run(() => { gate.Wait(); tcp2.Dispose(); });
            gate.Set();
            Assert.True(Task.WaitAll(new[] { t1, t2 }, 5000), "concurrent Dispose did not finish");
        }

        [Fact]
        public void Disconnect_AfterDispose_IsNoOp()
        {
            using var server = SilentTcpServer.Start();
            using var tcp = CreateClient(server.Port);
            Assert.True(ConnectWithRetry(tcp));
            tcp.Dispose();

            tcp.Disconnect();   // 不抛异常
        }

        [Fact]
        public async Task Dispose_ClosesSocket()
        {
            using var server = SilentTcpServer.Start();
            using var tcp = CreateClient(server.Port);
            Assert.True(ConnectWithRetry(tcp));

            tcp.Dispose();

            var sw = Stopwatch.StartNew();
            while (server.ClosedByPeerCount == 0 && sw.ElapsedMilliseconds < 5000)
                await Task.Delay(50);

            Assert.Equal(1, server.ClosedByPeerCount);
        }

        [Fact]
        public async Task RequestAsync_UserCancelsWhileNotDisposed_StillReturnsCancelled()
        {
            using var server = SilentTcpServer.Start();
            using var tcp = new ModbusTcpClient(new ModbusTcpClientConfig
            {
                Address = "127.0.0.1",
                Port = server.Port,
                ReadTimeout = 30000,
                WriteTimeout = 1000,
                ConnectTimeout = 10000,
                RetryCount = 0,
                Reconnect = false
            });
            Assert.True(ConnectWithRetry(tcp));

            using var cts = new CancellationTokenSource(200);
            var result = await tcp.RequestAsync(CreateReadHoldingRegistersRequest(), cts.Token);

            Assert.False(result.IsSuccess);
            Assert.Equal(ModbusErrorKind.Cancelled, result.ErrorKind);
        }

        // ————————————————— 私有辅助 —————————————————

        private const int RequestLength = 12;

        /// <summary>
        /// 本类每个用例循环 20 次、每次新建服务端与客户端，单轮测试就会新建数百个套接字；
        /// 在 Windows 上偶发因 TIME_WAIT / 临时端口压力导致首次 Connect 失败。这是环境容量问题
        /// 而非被测逻辑，故重试若干次再判定失败。
        /// </summary>
        private static bool ConnectWithRetry(ModbusTcpClient tcp, int attempts = 5)
        {
            for (int i = 0; i < attempts; i++)
            {
                if (tcp.Connect())
                    return true;

                Thread.Sleep(200);
            }

            return false;
        }

        private static bool WaitForReceivedBytes(SilentTcpServer server, int bytes, int timeoutMs)
        {
            var sw = Stopwatch.StartNew();
            while (server.ReceivedBytes < bytes && sw.ElapsedMilliseconds < timeoutMs)
                Thread.Sleep(20);

            return server.ReceivedBytes >= bytes;
        }

        private static async Task<bool> WaitForReceivedBytesAsync(SilentTcpServer server, int bytes, int timeoutMs)
        {
            var sw = Stopwatch.StartNew();
            while (server.ReceivedBytes < bytes && sw.ElapsedMilliseconds < timeoutMs)
                await Task.Delay(20);

            return server.ReceivedBytes >= bytes;
        }

        private static ModbusTcpClient CreateClient(int port)
            => new(new ModbusTcpClientConfig
            {
                Address = "127.0.0.1",
                Port = port,
                ReadTimeout = 10000,
                WriteTimeout = 1000,
                ConnectTimeout = 10000,
                RetryCount = 0,
                Reconnect = false
            });

        private static ModbusRequest CreateReadHoldingRegistersRequest()
            => new()
            {
                SlaveId = 1,
                FunctionCode = ModbusFunctionCode.ReadHoldingRegisters,
                StartAddress = 0,
                Quantity = 1
            };
    }
}