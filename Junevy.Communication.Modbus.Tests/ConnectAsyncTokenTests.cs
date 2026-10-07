using Junevy.Communication.Modbus.Core.Models;
using Junevy.Communication.Modbus.Tests.TestSupport;
using System.Diagnostics;

namespace Junevy.Communication.Modbus.Tests
{
    /// <summary>
    /// ConnectAsync 的取消令牌契约：令牌在"等待 requestLock"与"建立连接"两处生效；
    /// 用户取消抛出 OperationCanceledException（而非返回 false），连接失败/超时仍返回 false。
    /// </summary>
    public class ConnectAsyncTokenTests
    {
        [Fact]
        public async Task ConnectAsync_PreCancelledToken_ThrowsOperationCanceled()
        {
            var transport = new FakeTransport(reconnectEnabled: false, retryCount: 0, retryInterval: 0);
            using var cts = new CancellationTokenSource();
            cts.Cancel();

            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => transport.ConnectAsync(cts.Token));
            Assert.Equal(0, transport.OpenConnectionAsyncCalls);
        }

        [Fact]
        public async Task ConnectAsync_TokenIsPassedToOpenConnectionAsync()
        {
            var transport = new FakeTransport(reconnectEnabled: false, retryCount: 0, retryInterval: 0);
            CancellationToken observed = default;
            transport.OnOpenConnectionAsync = ct =>
            {
                observed = ct;
                return Task.FromResult(true);
            };

            using var cts = new CancellationTokenSource();
            Assert.True(await transport.ConnectAsync(cts.Token));

            Assert.Equal(cts.Token, observed);
        }

        [Fact]
        public async Task ConnectAsync_CancelledWhileWaitingForLock_Throws()
        {
            var transport = new FakeTransport(reconnectEnabled: false, retryCount: 0, retryInterval: 0);
            using var sendEntered = new ManualResetEventSlim(false);
            using var sendGate = new ManualResetEventSlim(false);
            transport.OnSendFrame = () =>
            {
                // 已在请求锁内：先放行"已进入"的信号，再阻塞等待放行门
                sendEntered.Set();
                sendGate.Wait();
                return true;
            };

            // 先启动一个请求占用 requestLock
            var inFlight = Task.Run(() => transport.Request(CreateReadHoldingRegistersRequest()));
            Assert.True(sendEntered.Wait(TimeSpan.FromSeconds(10)), "in-flight request never reached SendFrame");

            using var cts = new CancellationTokenSource(100);
            var sw = Stopwatch.StartNew();
            var connectTask = transport.ConnectAsync(cts.Token);
            var completed = await Task.WhenAny(connectTask, Task.Delay(5000));
            sw.Stop();

            Assert.True(completed == connectTask, $"ConnectAsync did not complete within 5000ms (elapsed {sw.ElapsedMilliseconds}ms)");
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => connectTask);
            Assert.Equal(0, transport.OpenConnectionAsyncCalls);

            sendGate.Set();
            var result = await inFlight;
            Assert.True(result.IsSuccess, result.ErrorMessage);
        }

        [Fact]
        public async Task ConnectAsync_CancelledDuringOpen_Throws()
        {
            var transport = new FakeTransport(reconnectEnabled: false, retryCount: 0, retryInterval: 0)
            {
                OnOpenConnectionAsync = async ct =>
                {
                    await Task.Delay(Timeout.Infinite, ct);
                    return false;
                }
            };

            using var cts = new CancellationTokenSource(100);
            var sw = Stopwatch.StartNew();
            var connectTask = transport.ConnectAsync(cts.Token);
            var completed = await Task.WhenAny(connectTask, Task.Delay(5000));
            sw.Stop();

            Assert.True(completed == connectTask, $"ConnectAsync did not complete within 5000ms (elapsed {sw.ElapsedMilliseconds}ms)");
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => connectTask);
            Assert.True(sw.ElapsedMilliseconds < 1500, $"Cancellation took {sw.ElapsedMilliseconds}ms");
        }

        [Fact]
        public async Task ConnectAsync_DefaultToken_StillWorks()
        {
            var transport = new FakeTransport(reconnectEnabled: false, retryCount: 0, retryInterval: 0);

            Assert.True(await transport.ConnectAsync());
            Assert.Equal(1, transport.OpenConnectionAsyncCalls);
        }

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