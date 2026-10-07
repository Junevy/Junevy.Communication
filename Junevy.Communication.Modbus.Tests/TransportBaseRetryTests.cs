using Junevy.Communication.Modbus.Core.Models;
using Junevy.Communication.Modbus.Tcp;
using Junevy.Communication.Modbus.Tests.TestSupport;
using System.Diagnostics;

namespace Junevy.Communication.Modbus.Tests
{
    /// <summary>
    /// 重试与重连解耦验证：Reconnect=false 时，需要重建连接的失败（超时、连接关闭、发送失败）
    /// 立即返回真实错误，不再空转重试并被覆盖为 "Not connected"；未连接且 Reconnect=false 时
    /// 请求立即返回。Reconnect=true 的行为不变。
    /// </summary>
    public class TransportBaseRetryTests
    {
        // ————————————————— 同步 Request —————————————————

        [Fact]
        public void Request_NoReconnect_TimeoutDoesNotRetry()
        {
            var transport = CreateTransport(reconnectEnabled: false, retryCount: 3, retryInterval: 1000);
            transport.ReceiveResults.Enqueue(ModbusResult<byte[]>.Fail("t", ModbusErrorKind.Timeout));

            var sw = Stopwatch.StartNew();
            var result = transport.Request(CreateRequest());
            sw.Stop();

            Assert.False(result.IsSuccess);
            Assert.Equal(ModbusErrorKind.Timeout, result.ErrorKind);
            Assert.Equal(1, transport.SendFrameCalls);
            Assert.Equal(1, transport.InvalidateConnectionCalls);
            Assert.True(sw.ElapsedMilliseconds < 500, $"Expected immediate return, took {sw.ElapsedMilliseconds}ms");
        }

        [Fact]
        public void Request_Reconnect_TimeoutRetriesWithNewConnections()
        {
            var transport = CreateTransport(reconnectEnabled: true, retryCount: 3, retryInterval: 10);
            for (int i = 0; i < 4; i++)
                transport.ReceiveResults.Enqueue(ModbusResult<byte[]>.Fail("t", ModbusErrorKind.Timeout));

            var result = transport.Request(CreateRequest());

            Assert.False(result.IsSuccess);
            Assert.Equal(ModbusErrorKind.Timeout, result.ErrorKind);
            Assert.Equal(4, transport.SendFrameCalls);
            Assert.Equal(3, transport.OpenConnectionCalls);
        }

        [Fact]
        public void Request_NoReconnect_NotConnectedReturnsImmediately()
        {
            var transport = CreateTransport(reconnectEnabled: false, retryCount: 3, retryInterval: 1000);
            transport.Connected = false;

            var sw = Stopwatch.StartNew();
            var result = transport.Request(CreateRequest());
            sw.Stop();

            Assert.False(result.IsSuccess);
            Assert.Equal(ModbusErrorKind.ConnectionClosed, result.ErrorKind);
            Assert.Equal(0, transport.SendFrameCalls);
            Assert.Equal(0, transport.OpenConnectionCalls);
            Assert.True(sw.ElapsedMilliseconds < 500, $"Expected immediate return, took {sw.ElapsedMilliseconds}ms");
        }

        [Fact]
        public void Request_NoReconnect_ProtocolViolationRetriesOnSameConnection()
        {
            var transport = CreateTransport(reconnectEnabled: false, retryCount: 2, retryInterval: 10);
            for (int i = 0; i < 3; i++)
                transport.ReceiveResults.Enqueue(ModbusResult<byte[]>.Fail("p", ModbusErrorKind.ProtocolViolation));

            var result = transport.Request(CreateRequest());

            // ProtocolViolation 不需要换连接：同一连接上重试，不销毁连接
            Assert.Equal(3, transport.SendFrameCalls);
            Assert.Equal(0, transport.InvalidateConnectionCalls);
            Assert.False(result.IsSuccess);
        }

        [Fact]
        public void Request_NoReconnect_SendFailureDoesNotRetry()
        {
            var transport = CreateTransport(reconnectEnabled: false, retryCount: 3, retryInterval: 1000);
            transport.OnSendFrame = () => false;

            var sw = Stopwatch.StartNew();
            var result = transport.Request(CreateRequest());
            sw.Stop();

            Assert.False(result.IsSuccess);
            Assert.Equal(ModbusErrorKind.ConnectionClosed, result.ErrorKind);
            Assert.Equal(1, transport.SendFrameCalls);
            Assert.True(sw.ElapsedMilliseconds < 500, $"Expected immediate return, took {sw.ElapsedMilliseconds}ms");
        }

        [Fact]
        public void Request_ModbusException_IsTerminal()
        {
            var transport = CreateTransport(reconnectEnabled: true, retryCount: 3, retryInterval: 1000);
            transport.ReceiveResults.Enqueue(ModbusResult<byte[]>.Fail("e", ModbusErrorKind.ModbusException));

            var result = transport.Request(CreateRequest());

            Assert.False(result.IsSuccess);
            Assert.Equal(ModbusErrorKind.ModbusException, result.ErrorKind);
            Assert.Equal(1, transport.SendFrameCalls);
        }

        // ————————————————— 异步 RequestAsync —————————————————

        [Fact]
        public async Task RequestAsync_NoReconnect_TimeoutDoesNotRetry()
        {
            var transport = CreateTransport(reconnectEnabled: false, retryCount: 3, retryInterval: 1000);
            transport.ReceiveResults.Enqueue(ModbusResult<byte[]>.Fail("t", ModbusErrorKind.Timeout));

            var sw = Stopwatch.StartNew();
            var result = await transport.RequestAsync(CreateRequest());
            sw.Stop();

            Assert.False(result.IsSuccess);
            Assert.Equal(ModbusErrorKind.Timeout, result.ErrorKind);
            Assert.Equal(1, transport.SendFrameCalls);
            Assert.Equal(1, transport.InvalidateConnectionCalls);
            Assert.True(sw.ElapsedMilliseconds < 500, $"Expected immediate return, took {sw.ElapsedMilliseconds}ms");
        }

        [Fact]
        public async Task RequestAsync_NoReconnect_NotConnectedReturnsImmediately()
        {
            var transport = CreateTransport(reconnectEnabled: false, retryCount: 3, retryInterval: 1000);
            transport.Connected = false;

            var sw = Stopwatch.StartNew();
            var result = await transport.RequestAsync(CreateRequest());
            sw.Stop();

            Assert.False(result.IsSuccess);
            Assert.Equal(ModbusErrorKind.ConnectionClosed, result.ErrorKind);
            Assert.Equal(0, transport.SendFrameCalls);
            Assert.Equal(0, transport.OpenConnectionCalls);
            Assert.True(sw.ElapsedMilliseconds < 500, $"Expected immediate return, took {sw.ElapsedMilliseconds}ms");
        }

        // ————————————————— 真实 TCP 客户端（默认配置） —————————————————

        [Fact]
        public async Task TcpClient_DefaultConfig_ServerNeverReplies_SendsExactlyOneRequest()
        {
            using var server = SilentTcpServer.Start();
            using var tcp = new ModbusTcpClient(new ModbusTcpClientConfig
            {
                Address = "127.0.0.1",
                Port = server.Port,
                ReadTimeout = 300,
                WriteTimeout = 1000,
                ConnectTimeout = 10000
                // Reconnect=false、RetryCount=3 取默认值
            });
            Assert.True(tcp.Connect());

            var sw = Stopwatch.StartNew();
            var result = await tcp.RequestAsync(new ModbusRequest
            {
                SlaveId = 1,
                FunctionCode = ModbusFunctionCode.ReadHoldingRegisters,
                StartAddress = 0,
                Quantity = 1
            });
            sw.Stop();

            Assert.False(result.IsSuccess);
            Assert.Equal(ModbusErrorKind.Timeout, result.ErrorKind);
            Assert.Equal(12, server.ReceivedBytes);
            Assert.True(sw.ElapsedMilliseconds < 1200, $"Expected single-attempt return, took {sw.ElapsedMilliseconds}ms");
        }

        // ————————————————— 私有辅助 —————————————————

        private static FakeTransport CreateTransport(bool reconnectEnabled, int retryCount, int retryInterval)
            => new(reconnectEnabled, retryCount, retryInterval);

        private static ModbusRequest CreateRequest()
            => new()
            {
                SlaveId = 1,
                FunctionCode = ModbusFunctionCode.ReadHoldingRegisters,
                StartAddress = 0,
                Quantity = 1
            };
    }
}
