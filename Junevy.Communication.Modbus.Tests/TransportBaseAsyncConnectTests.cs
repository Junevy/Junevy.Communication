using Junevy.Communication.Modbus.Core.Models;
using Junevy.Communication.Modbus.Tests.TestSupport;
using System.Diagnostics;

namespace Junevy.Communication.Modbus.Tests
{
    /// <summary>
    /// 基类异步连接路径验证：EnsureConnectedAsync 必须走 OpenConnectionAsync（真异步、可取消），
    /// 而非 Task.Run 包装的同步 OpenConnection（占用线程池线程且取消令牌无效）。
    /// </summary>
[Collection(SocketTimingCollection.Name)]
    public class TransportBaseAsyncConnectTests
    {
        [Fact]
        public async Task RequestAsync_Reconnect_UsesAsyncOpenNotSyncOpen()
        {
            var transport = new FakeTransport(reconnectEnabled: true, retryCount: 0, retryInterval: 0)
            {
                Connected = false
            };

            var result = await transport.RequestAsync(CreateReadHoldingRegistersRequest());

            Assert.True(result.IsSuccess, result.ErrorMessage);
            Assert.Equal(1, transport.OpenConnectionAsyncCalls);
            Assert.Equal(0, transport.OpenConnectionCalls);
        }

        [Fact]
        public async Task RequestAsync_CancelledDuringReconnect_ReturnsCancelled()
        {
            var transport = new FakeTransport(reconnectEnabled: true, retryCount: 0, retryInterval: 0)
            {
                Connected = false,
                // 异步连接钩子挂起直到令牌取消（抛出 TaskCanceledException）
                OnOpenConnectionAsync = async ct =>
                {
                    await Task.Delay(Timeout.Infinite, ct);
                    return false;
                }
            };

            using var cts = new CancellationTokenSource(100);
            var sw = Stopwatch.StartNew();
            var requestTask = transport.RequestAsync(CreateReadHoldingRegistersRequest(), cts.Token);
            var completed = await Task.WhenAny(requestTask, Task.Delay(1500));
            sw.Stop();

            Assert.True(completed == requestTask, $"RequestAsync did not complete within 1500ms (elapsed {sw.ElapsedMilliseconds}ms)");
            var result = await requestTask;
            Assert.False(result.IsSuccess);
            Assert.Equal(ModbusErrorKind.Cancelled, result.ErrorKind);
        }

        [Fact]
        public async Task Connect_UsesLockedAsyncOpen()
        {
            var transport = new FakeTransport(reconnectEnabled: false, retryCount: 0, retryInterval: 0)
            {
                Connected = false
            };

            bool connected = await transport.ConnectAsync();

            Assert.True(connected);
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
