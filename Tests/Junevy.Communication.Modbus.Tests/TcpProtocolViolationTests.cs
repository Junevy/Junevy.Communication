using Junevy.Communication.Modbus.Core.Models;
using Junevy.Communication.Modbus.Extensions;
using Junevy.Communication.Modbus.Tcp;
using Junevy.Communication.Modbus.Tests.TestSupport;
using System.Net.Sockets;

namespace Junevy.Communication.Modbus.Tests
{
    /// <summary>
    /// TCP 收到 ProtocolViolation（事务 ID 不匹配、非法 PDU 长度等）后必须销毁连接：
    /// 流中往往还有未读字节或迟到的响应，复用同一连接会让下一次请求读到残帧。
    /// Modbus 异常响应是完整合法的应答，不应销毁连接。
    /// </summary>
[Collection(SocketTimingCollection.Name)]
    public class TcpProtocolViolationTests
    {
        private const int RequestLength = 12;

        [Fact]
        public async Task TidMismatch_NoReconnect_ConnectionIsDestroyed()
        {
            int requestCount = 0;
            using var server = ScriptedTcpServer.Start((index, stream, ct) =>
                ServeRequestsAsync(stream, ct, request =>
                {
                    Interlocked.Increment(ref requestCount);
                    // 事务 ID 故意 +1
                    var response = ScriptedTcpServer.ReadHoldingRegistersResponse(request);
                    response[0] = (byte)(response[0] + 1);
                    return response;
                }));
            using var tcp = CreateClient(server.Port, reconnect: false, retryCount: 3);

            Assert.True(tcp.Connect());
            var result = await tcp.ReadHoldingRegistersAsync(1, 0, 1);

            Assert.False(result.IsSuccess);
            Assert.Equal(ModbusErrorKind.ProtocolViolation, result.ErrorKind);
            Assert.False(tcp.IsConnected);
            Assert.Equal(1, server.AcceptedConnectionCount);
            Assert.Equal(1, Volatile.Read(ref requestCount));
        }

        [Fact]
        public async Task TidMismatch_Reconnect_RetriesOnNewConnection()
        {
            using var server = ScriptedTcpServer.Start((index, stream, ct) =>
                ServeRequestsAsync(stream, ct, request =>
                {
                    if (index == 0)
                    {
                        var bad = ScriptedTcpServer.ReadHoldingRegistersResponse(request);
                        bad[0] = (byte)(bad[0] + 1);
                        return bad;
                    }

                    return ScriptedTcpServer.ReadHoldingRegistersResponse(request, value: 1);
                }));
            using var tcp = CreateClient(server.Port, reconnect: true, retryCount: 1, retryInterval: 10);

            Assert.True(tcp.Connect());
            var result = await tcp.ReadHoldingRegistersAsync(1, 0, 1);

            Assert.True(result.IsSuccess, result.ErrorMessage);
            Assert.NotNull(result.Data);
            Assert.Single(result.Data!);
            Assert.Equal(1, result.Data![0]);
            Assert.Equal(2, server.AcceptedConnectionCount);
        }

        [Fact]
        public async Task InvalidPduLength_Reconnect_NextRequestUsesFreshConnection()
        {
            using var server = ScriptedTcpServer.Start((index, stream, ct) =>
            {
                if (index != 0)
                    return ServeRequestsAsync(stream, ct, request => ScriptedTcpServer.ReadHoldingRegistersResponse(request));

                return ServeRequestsAsync(stream, ct, request =>
                {
                    // 长度字段 300（越界）+ 2 字节垃圾：客户端读到 6 字节头即判违规，负载留在流里
                    byte[] bad =
                    [
                        request[0], request[1],
                        0x00, 0x00,
                        0x01, 0x2C,
                        request[6],
                        0x03, 0x02
                    ];
                    return bad;
                });
            });
            using var tcp = CreateClient(server.Port, reconnect: true, retryCount: 0);

            Assert.True(tcp.Connect());
            var first = await tcp.ReadHoldingRegistersAsync(1, 0, 1);

            Assert.False(first.IsSuccess);
            Assert.Equal(ModbusErrorKind.ProtocolViolation, first.ErrorKind);

            var second = await tcp.ReadHoldingRegistersAsync(1, 0, 1);

            Assert.True(second.IsSuccess, second.ErrorMessage);
            Assert.Equal(2, server.AcceptedConnectionCount);
        }

        [Fact]
        public async Task ModbusException_DoesNotDestroyConnection()
        {
            int requestCount = 0;
            using var server = ScriptedTcpServer.Start((index, stream, ct) =>
                ServeRequestsAsync(stream, ct, request =>
                {
                    if (Interlocked.Increment(ref requestCount) == 1)
                    {
                        return new byte[]
                        {
                            request[0], request[1],
                            0x00, 0x00,
                            0x00, 0x03,
                            request[6],
                            0x83, 0x02
                        };
                    }

                    return ScriptedTcpServer.ReadHoldingRegistersResponse(request);
                }));
            using var tcp = CreateClient(server.Port, reconnect: false, retryCount: 3);

            Assert.True(tcp.Connect());
            var first = await tcp.ReadHoldingRegistersAsync(1, 0, 1);

            Assert.False(first.IsSuccess);
            Assert.Equal(ModbusErrorKind.ModbusException, first.ErrorKind);
            Assert.True(tcp.IsConnected);

            var second = await tcp.ReadHoldingRegistersAsync(1, 0, 1);

            Assert.True(second.IsSuccess, second.ErrorMessage);
            Assert.Equal(1, server.AcceptedConnectionCount);
            Assert.Equal(2, Volatile.Read(ref requestCount));
        }

        // ————————————————— 私有辅助 —————————————————

        /// <summary>
        /// 在一条连接上循环读取请求帧，并对每个请求返回 <paramref name="respond"/> 给出的响应。
        /// 连接被对端关闭或服务端关闭时结束循环。
        /// </summary>
        private static async Task ServeRequestsAsync(
            NetworkStream stream,
            CancellationToken shutdownToken,
            Func<byte[], byte[]> respond)
        {
            var request = new byte[RequestLength];
            try
            {
                while (!shutdownToken.IsCancellationRequested)
                {
                    if (!await ScriptedTcpServer.ReadExactAsync(stream, request, RequestLength))
                        return;

                    await stream.WriteAsync(respond(request));
                    await stream.FlushAsync();
                }
            }
            catch (Exception ex) when (ex is IOException or ObjectDisposedException or SocketException or OperationCanceledException)
            {
                // 客户端销毁连接或服务端关闭：正常收尾
            }
        }

        private static ModbusTcpClient CreateClient(int port, bool reconnect, int retryCount, int retryInterval = 100)
            => new(new ModbusTcpClientConfig
            {
                Address = "127.0.0.1",
                Port = port,
                ReadTimeout = 5000,
                WriteTimeout = 1000,
                ConnectTimeout = 10000,
                RetryCount = retryCount,
                RetryInterval = retryInterval,
                Reconnect = reconnect
            });
    }
}