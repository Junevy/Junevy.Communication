using Junevy.Communication.Modbus.Core.Models;
using Junevy.Communication.Modbus.Extensions;
using Junevy.Communication.Modbus.Tcp;
using Junevy.Communication.Modbus.Tests.TestSupport;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;

namespace Junevy.Communication.Modbus.Tests
{
    /// <summary>
    /// 异步路径超时验证：ReadTimeout / WriteTimeout 必须对 NetworkStream.ReadAsync/WriteAsync 生效
    /// （此前 Socket.ReceiveTimeout/SendTimeout 只约束同步 I/O，服务端不应答时异步请求永久挂起）。
    /// </summary>
    public class TcpAsyncTimeoutTests
    {
        [Fact]
        public async Task RequestAsync_ServerNeverReplies_ReturnsTimeout()
        {
            using var server = SilentTcpServer.Start();
            using var tcp = CreateClient(server.Port, readTimeout: 500);
            Assert.True(tcp.Connect());

            var sw = Stopwatch.StartNew();
            var requestTask = tcp.RequestAsync(CreateReadHoldingRegistersRequest());
            var completed = await Task.WhenAny(requestTask, Task.Delay(10000));
            sw.Stop();

            Assert.True(completed == requestTask, $"RequestAsync did not complete within 10s (elapsed {sw.ElapsedMilliseconds}ms)");
            var result = await requestTask;
            Assert.False(result.IsSuccess);
            Assert.Equal(ModbusErrorKind.Timeout, result.ErrorKind);
            Assert.InRange(sw.ElapsedMilliseconds, 450, 2500);
        }

        [Fact]
        public async Task RequestAsync_ServerSendsPartialHeader_ReturnsTimeout()
        {
            using var server = SilentTcpServer.Start();
            // 只发半个响应头（3 字节），永远凑不齐 6 字节 MBAP 头
            server.OnConnected = stream => stream.Write(new byte[] { 0x00, 0x00, 0x00 }, 0, 3);
            using var tcp = CreateClient(server.Port, readTimeout: 500);
            Assert.True(tcp.Connect());

            var sw = Stopwatch.StartNew();
            var requestTask = tcp.RequestAsync(CreateReadHoldingRegistersRequest());
            var completed = await Task.WhenAny(requestTask, Task.Delay(10000));
            sw.Stop();

            Assert.True(completed == requestTask, $"RequestAsync did not complete within 10s (elapsed {sw.ElapsedMilliseconds}ms)");
            var result = await requestTask;
            Assert.False(result.IsSuccess);
            Assert.Equal(ModbusErrorKind.Timeout, result.ErrorKind);
            Assert.InRange(sw.ElapsedMilliseconds, 450, 2500);
        }

        [Fact]
        public async Task ReadHoldingRegistersAsync_ServerNeverReplies_ReturnsWithinReadTimeout()
        {
            using var server = SilentTcpServer.Start();
            using var tcp = CreateClient(server.Port, readTimeout: 500);
            Assert.True(tcp.Connect());

            var valueTask = tcp.ReadHoldingRegistersAsync(1, 0, 1);
            var requestTask = valueTask.AsTask();
            var completed = await Task.WhenAny(requestTask, Task.Delay(10000));

            Assert.True(completed == requestTask, "ReadHoldingRegistersAsync did not complete within 10s");
            var result = await requestTask;
            // ErrorKind == Timeout 的断言在计划四（高层 API 保留 ErrorKind）合入后回补
            Assert.False(result.IsSuccess);
        }

        [Fact]
        public async Task RequestAsync_ServerRepliesWithinTimeout_Succeeds()
        {
            // 自定义服务端：收到 12 字节请求后等待 200ms，再回复"读 1 个寄存器"响应（回显 TID，值 00 01）
            var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            var port = ((IPEndPoint)listener.LocalEndpoint).Port;
            var server = Task.Run(async () =>
            {
                try
                {
                    using var handler = await listener.AcceptTcpClientAsync();
                    var stream = handler.GetStream();
                    var request = new byte[12];
                    int read = 0;
                    while (read < request.Length)
                    {
                        int n = await stream.ReadAsync(request, read, request.Length - read);
                        if (n == 0)
                            return;
                        read += n;
                    }

                    await Task.Delay(200);
                    byte[] response = [request[0], request[1], 0x00, 0x00, 0x00, 0x05, 0x01, 0x03, 0x02, 0x00, 0x01];
                    await stream.WriteAsync(response);
                }
                catch
                {
                    // listener.Stop()/客户端提前弃连引发的异常，正常退出
                }
            });

            // 并发负载下（如工厂并发冒烟测试阻塞线程池），服务器任务可能被延迟调度；
            // ReadTimeout 放宽到 10000 覆盖调度延迟（与同仓库既有 TCP 测试一致），避免误报超时。
            // 本用例的判定核心是"服务端延迟 200ms 应答 → 成功"，证明超时机制不误触发。
            using var tcp = CreateClient(port, readTimeout: 10000);
            Assert.True(tcp.Connect());

            var sw = Stopwatch.StartNew();
            var requestTask = tcp.RequestAsync(CreateReadHoldingRegistersRequest());
            var completed = await Task.WhenAny(requestTask, Task.Delay(10000));
            sw.Stop();

            Assert.True(completed == requestTask, $"RequestAsync did not complete within 10s (elapsed {sw.ElapsedMilliseconds}ms)");
            var result = await requestTask;
            listener.Stop();
            try { await server.WaitAsync(TimeSpan.FromSeconds(15)); }
            catch (TimeoutException) { /* 断言不依赖服务端退出 */ }

            Assert.True(result.IsSuccess, result.ErrorMessage);
        }

        [Fact]
        public async Task RequestAsync_UserCancelsBeforeReadTimeout_ReturnsCancelled()
        {
            using var server = SilentTcpServer.Start();
            using var tcp = CreateClient(server.Port, readTimeout: 30000);
            Assert.True(tcp.Connect());

            using var cts = new CancellationTokenSource(200);
            var sw = Stopwatch.StartNew();
            var requestTask = tcp.RequestAsync(CreateReadHoldingRegistersRequest(), cts.Token);
            var completed = await Task.WhenAny(requestTask, Task.Delay(10000));
            sw.Stop();

            Assert.True(completed == requestTask, $"RequestAsync did not complete within 10s (elapsed {sw.ElapsedMilliseconds}ms)");
            var result = await requestTask;
            Assert.False(result.IsSuccess);
            Assert.Equal(ModbusErrorKind.Cancelled, result.ErrorKind);
            // 200ms 取消的容限与既有 TcpClient_RequestAsync_CancelledDuringRead_ReturnsCancelledQuickly
            // 的 5s 惯例一致：全量并行下线程池调度延迟会放大取消传播耗时
            Assert.True(sw.ElapsedMilliseconds < 5000, $"Cancellation took {sw.ElapsedMilliseconds}ms");
        }

        [Fact]
        public async Task RequestAsync_ReadTimeout_ConnectionIsInvalidatedAfterTimeout()
        {
            using var server = SilentTcpServer.Start();
            using var tcp = CreateClient(server.Port, readTimeout: 300);
            Assert.True(tcp.Connect());

            var requestTask = tcp.RequestAsync(CreateReadHoldingRegistersRequest());
            var completed = await Task.WhenAny(requestTask, Task.Delay(10000));
            Assert.True(completed == requestTask, "RequestAsync did not complete within 10s");
            await requestTask;

            Assert.False(tcp.IsConnected);
        }

        // ————————————————— 私有辅助 —————————————————

        private static ModbusTcpClient CreateClient(int port, int readTimeout)
            => new(new ModbusTcpClientConfig
            {
                Address = "127.0.0.1",
                Port = port,
                ReadTimeout = readTimeout,
                WriteTimeout = 1000,
                // 并发负载下（如工厂并发冒烟测试阻塞线程池），连接等待可能超支预算，放宽以覆盖调度延迟
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
