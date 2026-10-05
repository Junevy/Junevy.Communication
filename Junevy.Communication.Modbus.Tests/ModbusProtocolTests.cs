using Junevy.Communication.Modbus.Core.Framing;
using Junevy.Communication.Modbus.Core.Models;
using Junevy.Communication.Modbus.Core.Parsing;
using Junevy.Communication.Modbus.Rtu;
using Junevy.Communication.Modbus.Tcp;
using Junevy.Communication.Modbus.Utils;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;

namespace Junevy.Communication.Modbus.Tests
{
    public class ModbusProtocolTests
    {
        [Fact]
        public void BuildRequestFrame_MaskWriteRegister_RtuFrameIsValid()
        {
            var request = new ModbusRequest
            {
                ProtocolType = ModbusProtocolType.RTU,
                SlaveId = 1,
                FunctionCode = ModbusFunctionCode.MaskWriteRegister,
                StartAddress = 0x1234,
                Data = [0xFF, 0x00, 0x00, 0xF0]
            };

            var frame = ModbusHelper.BuildRequestFrame(request);

            Assert.Equal(10, frame.Length);
            Assert.Equal([0x01, 0x16, 0x12, 0x34, 0xFF, 0x00, 0x00, 0xF0], frame[..8]);
            Assert.True(Crc16Helper.VerifyCrc(frame));
        }

        [Fact]
        public void BuildRequestFrame_ReadWriteMultipleRegisters_UsesPreEncodedData()
        {
            var request = new ModbusRequest
            {
                ProtocolType = ModbusProtocolType.RTU,
                SlaveId = 1,
                FunctionCode = ModbusFunctionCode.ReadWriteMultipleRegisters,
                Data =
                [
                    0x00, 0x10,
                    0x00, 0x02,
                    0x00, 0x20,
                    0x00, 0x01,
                    0x02,
                    0x12, 0x34
                ]
            };

            var frame = ModbusHelper.BuildRequestFrame(request);

            Assert.Equal(15, frame.Length);
            Assert.Equal(0x17, frame[1]);
            Assert.True(Crc16Helper.VerifyCrc(frame));
        }

        [Fact]
        public void FrameBuilder_TryWriteTcpRequestFrame_WritesWithoutCompatibilityArray()
        {
            var request = new ModbusRequest
            {
                ProtocolType = ModbusProtocolType.TCP,
                TransactionId = 0,
                SlaveId = 2,
                FunctionCode = ModbusFunctionCode.ReadHoldingRegisters,
                StartAddress = 0x0010,
                Quantity = 2
            };
            var builder = new ModbusFrameBuilder();
            Span<byte> destination = stackalloc byte[ModbusFrameBuilder.MaxTcpAduLength];

            Assert.True(builder.TryWriteRequestFrame(request, ModbusProtocolType.TCP, destination, out int written));

            Assert.Equal(12, written);
            Assert.Equal([0x00, 0x00, 0x00, 0x00, 0x00, 0x06, 0x02, 0x03, 0x00, 0x10, 0x00, 0x02],
                destination[..written].ToArray());
        }

        [Fact]
        public void FrameBuilder_ProtocolComesFromArgumentNotRequest()
        {
            var request = new ModbusRequest
            {
                ProtocolType = ModbusProtocolType.RTU,   // 故意设错
                SlaveId = 2,
                FunctionCode = ModbusFunctionCode.ReadHoldingRegisters,
                StartAddress = 0x0010,
                Quantity = 2
            };
            var builder = new ModbusFrameBuilder();
            Span<byte> destination = stackalloc byte[ModbusFrameBuilder.MaxTcpAduLength];

            Assert.True(builder.TryWriteRequestFrame(request, ModbusProtocolType.TCP, destination, out int written));
            Assert.Equal(12, written);
            // TCP 帧应有 MBAP 头（前 6 字节非从站 ID 开头）
            Assert.Equal(0x00, destination[2]); // protocol id hi

            // 且 request 对象未被修改
            Assert.Equal(ModbusProtocolType.RTU, request.ProtocolType);
        }

        [Fact]
        public void RtuFrameBuilder_MaxRtuAduLength_Is256()
        {
            // 缓冲必须比最大 ADU 大 1 字节，使"读满 256 字节完整帧"不会触发溢出分支
            Assert.Equal(256, ModbusFrameBuilder.MaxRtuAduLength);
        }

        [Fact]
        public void CheckRequest_Diagnostics_OneByteData_IsValid()
        {
            var request = new ModbusRequest
            {
                ProtocolType = ModbusProtocolType.RTU,
                SlaveId = 1,
                FunctionCode = ModbusFunctionCode.Diagnostics,
                Data = [0x00, 0x00, 0xA5]   // sub=0x0000 + 1 字节数据
            };
            Assert.True(ModbusHelper.CheckRequest(request));
        }

        [Fact]
        public void RtuParser_WriteMultiShortFrame_ReturnsFailureInsteadOfThrowing()
        {
            var parser = new RtuProtocolParser();
            var request = new ModbusRequest
            {
                SlaveId = 1,
                FunctionCode = ModbusFunctionCode.WriteMultipleHoldingRegisters,
                StartAddress = 0x0010,
                Quantity = 2,
                Data = [0x00, 0x01, 0x00, 0x02]
            };

            byte[] response = [0x01, 0x10, 0x00, 0x10, 0x00];
            var result = parser.ParseResponse(response, request);

            Assert.False(result.IsSuccess);
        }

        [Fact]
        public void RtuParser_WriteMultiInvalidCrc_ReturnsFailure()
        {
            var parser = new RtuProtocolParser();
            var request = new ModbusRequest
            {
                SlaveId = 1,
                FunctionCode = ModbusFunctionCode.WriteMultipleHoldingRegisters,
                StartAddress = 0x0010,
                Quantity = 2,
                Data = [0x00, 0x01, 0x00, 0x02]
            };
            byte[] response = [0x01, 0x10, 0x00, 0x10, 0x00, 0x02, 0x00, 0x00];

            var result = parser.ParseResponse(response, request);

            Assert.False(result.IsSuccess);
        }

        [Fact]
        public void RtuParser_WriteMultiValidCrc_ReturnsExactFrame()
        {
            var parser = new RtuProtocolParser();
            var request = new ModbusRequest
            {
                SlaveId = 1,
                FunctionCode = ModbusFunctionCode.WriteMultipleHoldingRegisters,
                StartAddress = 0x0010,
                Quantity = 2,
                Data = [0x00, 0x01, 0x00, 0x02]
            };
            byte[] response = [0x01, 0x10, 0x00, 0x10, 0x00, 0x02, 0x00, 0x00];
            var crc = Crc16Helper.CrcLittleEndian(response.AsSpan(0, 6));
            response[6] = crc[0];
            response[7] = crc[1];

            var result = parser.ParseResponse(response, request);

            Assert.True(result.IsSuccess);
            Assert.Equal(8, result.Data.Length);
            Assert.Equal(response, result.Data.ToArray());
        }

        [Fact]
        public void RtuParser_ExceptionResponse_ReturnsFailureWithCode()
        {
            var request = new ModbusRequest
            {
                SlaveId = 1,
                FunctionCode = ModbusFunctionCode.ReadHoldingRegisters
            };
            byte[] response = [0x01, 0x83, 0x02, 0x00, 0x00];
            var crc = Crc16Helper.CrcLittleEndian(response.AsSpan(0, 3));
            response[3] = crc[0];
            response[4] = crc[1];

            var result = new RtuProtocolParser().ParseResponse(response, request);

            Assert.False(result.IsSuccess);
            Assert.Contains("0x02", result.ErrorMessage);
        }

        [Fact]
        public void TcpParser_SlaveMismatch_ReturnsFailure()
        {
            var parser = new TcpProtocolParser();
            var request = new ModbusRequest
            {
                TransactionId = 0,
                SlaveId = 1,
                FunctionCode = ModbusFunctionCode.ReadHoldingRegisters,
                StartAddress = 0,
                Quantity = 1
            };
            byte[] response = [0x00, 0x00, 0x00, 0x00, 0x00, 0x05, 0x02, 0x03, 0x02, 0x12, 0x34];

            var result = parser.ParseResponse(response, request);

            Assert.False(result.IsSuccess);
        }

        [Fact]
        public void TcpParser_WriteMultiShortFrame_ReturnsFailureInsteadOfThrowing()
        {
            var parser = new TcpProtocolParser();
            var request = new ModbusRequest
            {
                TransactionId = 0,
                SlaveId = 1,
                FunctionCode = ModbusFunctionCode.WriteMultipleHoldingRegisters,
                StartAddress = 0x0010,
                Quantity = 2,
                Data = [0x00, 0x01, 0x00, 0x02]
            };
            byte[] response = [0x00, 0x01, 0x00, 0x00, 0x00, 0x03, 0x01, 0x10, 0x00];

            var result = parser.ParseResponse(response, request);

            Assert.False(result.IsSuccess);
        }

        [Fact]
        public void TcpParser_ExceptionResponse_ReturnsFailureWithCode()
        {
            var request = new ModbusRequest
            {
                TransactionId = 0,
                SlaveId = 1,
                FunctionCode = ModbusFunctionCode.ReadHoldingRegisters,
                StartAddress = 0,
                Quantity = 1
            };
            byte[] response = [0x00, 0x00, 0x00, 0x00, 0x00, 0x03, 0x01, 0x83, 0x02];

            var result = new TcpProtocolParser().ParseResponse(response, request);

            Assert.False(result.IsSuccess);
            Assert.Contains("0x02", result.ErrorMessage);
            Assert.Equal(9, result.Data.Length);
        }

        [Fact]
        public void TcpConfig_PortIsDirectlySettable()
        {
            var config = new ModbusTcpClientConfig { Port = 1502 };
            Assert.Equal(1502, config.Port);
        }

        [Fact]
        public void VerifyPort_AcceptsAnyValidPort()
        {
            Assert.True(ModbusHelper.VerifyPort(1));
            Assert.True(ModbusHelper.VerifyPort(502));
            Assert.True(ModbusHelper.VerifyPort(65535));
            Assert.False(ModbusHelper.VerifyPort(0));
            Assert.False(ModbusHelper.VerifyPort(65536));
        }

        [Fact]
        public void BuildRequestFrame_ReadExceptionStatus_RtuFrameIsValid()
        {
            var request = new ModbusRequest
            {
                ProtocolType = ModbusProtocolType.RTU,
                SlaveId = 1,
                FunctionCode = ModbusFunctionCode.ReadExceptionStatus
            };

            var frame = ModbusHelper.BuildRequestFrame(request);

            Assert.Equal(4, frame.Length);
            Assert.Equal(0x07, frame[1]);
            Assert.True(Crc16Helper.VerifyCrc(frame));
        }

        [Fact]
        public void BuildRequestFrame_Diagnostics_WritesSubFunctionAndData()
        {
            var request = new ModbusRequest
            {
                ProtocolType = ModbusProtocolType.RTU,
                SlaveId = 1,
                FunctionCode = ModbusFunctionCode.Diagnostics,
                Data = [0x00, 0x00, 0x12, 0x34]
            };

            var frame = ModbusHelper.BuildRequestFrame(request);

            Assert.Equal([0x01, 0x08, 0x00, 0x00, 0x12, 0x34], frame[..6]);
            Assert.True(Crc16Helper.VerifyCrc(frame));
        }

        [Fact]
        public void RtuParser_ReportServerId_ReturnsVariableLengthFrame()
        {
            var parser = new RtuProtocolParser();
            var request = new ModbusRequest
            {
                SlaveId = 1,
                FunctionCode = ModbusFunctionCode.ReportServerId
            };
            byte[] response = [0x01, 0x11, 0x03, 0x42, 0xFF, 0x10, 0x00, 0x00];
            var crc = Crc16Helper.CrcLittleEndian(response.AsSpan(0, 6));
            response[6] = crc[0];
            response[7] = crc[1];

            var result = parser.ParseResponse(response, request);

            Assert.True(result.IsSuccess);
            Assert.Equal(response, result.Data.ToArray());
        }

        [Fact]
        public void RtuTransport_DefaultRequestProtocol_IsNotRequiredForValidation()
        {
            var request = new ModbusRequest
            {
                FunctionCode = ModbusFunctionCode.ReadHoldingRegisters,
                StartAddress = 0,
                Quantity = 1
            };

            Assert.True(ModbusHelper.CheckRequest(request));
        }

        [Fact]
        public async Task TcpRequest_AutoConnectsWhenReconnectEnabled()
        {
            var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            var port = ((IPEndPoint)listener.LocalEndpoint).Port;

            var serverTask = Task.Run(async () =>
            {
                // 客户端失败重试时每次都会重建连接（RetryCount=3 → 最多 4 次尝试），
                // 因此按顺序接受最多 4 个连接，每个连接服务一个完整请求。
                for (int i = 0; i < 4; i++)
                {
                    TcpClient handler;
                    try
                    {
                        handler = await listener.AcceptTcpClientAsync();
                    }
                    catch (Exception ex) when (ex is ObjectDisposedException or InvalidOperationException
                        or IOException or SocketException)
                    {
                        break; // listener.Stop() 会使挂起/后续的 Accept 抛异常（.NET 8 在 Stop 后为 InvalidOperationException），正常退出
                    }

                    using (handler)
                    using (var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10)))
                    {
                        try
                        {
                            var stream = handler.GetStream();
                            var request = new byte[12];
                            int read = 0;
                            while (read < request.Length)
                            {
                                int n = await stream.ReadAsync(request, read, request.Length - read, cts.Token);
                                if (n == 0)
                                    break; // 对端已关闭：客户端超时后放弃旧连接，转到下一个连接
                                read += n;
                            }

                            if (read < request.Length)
                                continue;

                            // 响应每个完整请求：回显其 TID（重试沿用同一逻辑请求的事务 ID）
                            byte[] response = [request[0], request[1], 0x00, 0x00, 0x00, 0x05, 0x01, 0x03, 0x02, 0x12, 0x34];
                            await stream.WriteAsync(response, 0, response.Length, cts.Token);
                        }
                        catch (OperationCanceledException)
                        {
                            // 本连接超时，转到下一个连接
                        }
                        catch (Exception ex) when (ex is IOException or ObjectDisposedException)
                        {
                            // 客户端放弃旧连接引发的 IO 异常，转到下一个连接
                        }
                    }
                }
            });

            using var client = new ModbusTcpClient(new ModbusTcpClientConfig
            {
                Address = IPAddress.Loopback.ToString(),
                // 并发负载下（如工厂并发冒烟测试阻塞线程池），服务器任务可能被延迟调度；
                // 放宽读超时以覆盖调度延迟，避免误报超时。
                ReadTimeout = 10000,
                WriteTimeout = 1000,
                ConnectTimeout = 1000,
                Reconnect = true,
                RetryCount = 3,
                RetryInterval = 10
            });
            client.Config.Port = port;

            var result = client.Request(new ModbusRequest
            {
                SlaveId = 1,
                FunctionCode = ModbusFunctionCode.ReadHoldingRegisters,
                StartAddress = 0,
                Quantity = 1
            });

            listener.Stop();
            try { await serverTask.WaitAsync(TimeSpan.FromSeconds(15)); }
            catch (TimeoutException) { /* Stop 后 pending 的 Accept 可能永不完成（.NET 8 已知行为），断言不依赖其退出 */ }

            Assert.True(result.IsSuccess, result.ErrorMessage);
            Assert.Equal([0x00, 0x00, 0x00, 0x00, 0x00, 0x05, 0x01, 0x03, 0x02, 0x12, 0x34], result.Data);
        }

        [Fact]
        public void TcpParser_TransactionIdMustMatchExactly()
        {
            var request = new ModbusRequest
            {
                TransactionId = 7,
                SlaveId = 1,
                FunctionCode = ModbusFunctionCode.ReadHoldingRegisters,
                StartAddress = 0,
                Quantity = 1
            };
            // 旧约定下 (7+1) 会通过；精确匹配下必须失败
            byte[] response = [0x00, 0x08, 0x00, 0x00, 0x00, 0x05, 0x01, 0x03, 0x02, 0x12, 0x34];

            var result = new TcpProtocolParser().ParseResponse(response, request);

            Assert.False(result.IsSuccess);
            Assert.Contains("Transaction ID", result.ErrorMessage);
        }

        [Fact]
        public async Task TcpClient_AssignsSequentialTransactionIds()
        {
            var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            var port = ((IPEndPoint)listener.LocalEndpoint).Port;
            var seenTids = new List<byte[]>();
            var server = Task.Run(async () =>
            {
                // 并发负载下客户端读超时后会重连重试（RetryCount=3 → 最多 4 次连接），
                // 且重试沿用同一逻辑请求的事务 ID，因此按顺序接受最多 4 个连接，
                // 每个连接循环服务完整请求并回显其 TID。
                for (int i = 0; i < 4; i++)
                {
                    TcpClient handler;
                    try
                    {
                        handler = await listener.AcceptTcpClientAsync();
                    }
                    catch (Exception ex) when (ex is ObjectDisposedException or InvalidOperationException
                        or IOException or SocketException)
                    {
                        break; // listener.Stop() 会使挂起/后续的 Accept 抛异常（.NET 8 在 Stop 后为 InvalidOperationException），正常退出
                    }

                    using (handler)
                    {
                        var stream = handler.GetStream();
                        while (true)
                        {
                            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                            try
                            {
                                var req = new byte[12];
                                int read = 0;
                                while (read < req.Length)
                                {
                                    int n = await stream.ReadAsync(req, read, req.Length - read, cts.Token);
                                    if (n == 0)
                                        break; // 对端已关闭：转到下一个连接
                                    read += n;
                                }

                                if (read < req.Length)
                                    break;

                                seenTids.Add(req[..2].ToArray());
                                byte[] resp = [req[0], req[1], 0x00, 0x00, 0x00, 0x05, 0x01, 0x03, 0x02, 0x12, 0x34];
                                await stream.WriteAsync(resp, 0, resp.Length, cts.Token);
                            }
                            catch (OperationCanceledException)
                            {
                                break; // 本连接超时，转到下一个连接
                            }
                            catch (Exception ex) when (ex is IOException or ObjectDisposedException
                                or InvalidOperationException or SocketException)
                            {
                                break; // 客户端放弃旧连接引发的 IO 异常，转到下一个连接
                            }
                        }
                    }
                }
            });

            ModbusResult<byte[]> r1;
            ModbusResult<byte[]> r2;
            using (var tcp = new ModbusTcpClient(new ModbusTcpClientConfig
            {
                Address = "127.0.0.1",
                // 并发负载下（如工厂并发冒烟测试阻塞线程池），服务器任务可能被延迟调度；
                // 放宽读超时以覆盖调度延迟，避免误报超时。
                ReadTimeout = 10000,
                WriteTimeout = 1000,
                // 并发负载下（如工厂并发冒烟测试阻塞线程池），连接等待同样可能超支 1s 预算，放宽以覆盖调度延迟。
                ConnectTimeout = 10000,
                Reconnect = true
            }))
            {
                tcp.Config.Port = port;
                Assert.True(tcp.Connect());

                var request = new ModbusRequest { SlaveId = 1, FunctionCode = ModbusFunctionCode.ReadHoldingRegisters, StartAddress = 0, Quantity = 1 };
                r1 = tcp.Request(request);
                r2 = tcp.Request(request);
            }
            // 先释放客户端再等待服务器：关闭连接让服务器的下一次 ReadAsync 立即返回 0 并退出，
            // 避免服务器在 10s CTS 上空等造成测试尾部停顿。
            listener.Stop();
            try { await server.WaitAsync(TimeSpan.FromSeconds(15)); }
            catch (TimeoutException) { /* Stop 后 pending 的 Accept 可能永不完成（.NET 8 已知行为），断言不依赖其退出 */ }

            Assert.True(r1.IsSuccess, r1.ErrorMessage);
            Assert.True(r2.IsSuccess, r2.ErrorMessage);
            Assert.True(seenTids.Count >= 2, $"Expected at least 2 exchanges, got {seenTids.Count}: {string.Join(", ", seenTids.Select(t => BitConverter.ToString(t)))}");
            // 逻辑请求 1 的最终交换 TID 恒为 0、请求 2 恒为 1；重试会以相同 TID 重发，
            // 取"首次出现顺序去重"后的序列做断言，对重试扰动保持不变式。
            var distinctTids = new List<byte[]>();
            foreach (var tid in seenTids)
            {
                if (!distinctTids.Any(d => d.SequenceEqual(tid)))
                    distinctTids.Add(tid);
            }

            Assert.Equal(new[] { new byte[] { 0x00, 0x00 }, new byte[] { 0x00, 0x01 } }, distinctTids);
        }

        [Fact]
        public async Task TcpClient_ExceptionResponse_IsTerminal_NoRetry()
        {
            var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            var port = ((IPEndPoint)listener.LocalEndpoint).Port;
            var requestsReceived = 0;

            var server = Task.Run(async () =>
            {
                // 接受最多 2 个连接：若客户端错误地在异常响应上重试，会重连并再次发送请求。
                for (int i = 0; i < 2; i++)
                {
                    TcpClient handler;
                    try
                    {
                        handler = await listener.AcceptTcpClientAsync();
                    }
                    catch (Exception ex) when (ex is ObjectDisposedException or InvalidOperationException
                        or IOException or SocketException)
                    {
                        break; // listener.Stop() 会使挂起/后续的 Accept 抛异常（.NET 8 在 Stop 后为 InvalidOperationException），正常退出
                    }

                    using (handler)
                    using (var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10)))
                    {
                        try
                        {
                            var stream = handler.GetStream();
                            var request = new byte[12];
                            int read = 0;
                            while (read < request.Length)
                            {
                                int n = await stream.ReadAsync(request, read, request.Length - read, cts.Token);
                                if (n == 0)
                                    break;
                                read += n;
                            }

                            if (read < request.Length)
                                continue;

                            Interlocked.Increment(ref requestsReceived);

                            // 9 字节异常响应帧：回显请求 TID，funcCode=0x83（0x03|0x80），异常码=0x02
                            byte[] response = [request[0], request[1], 0x00, 0x00, 0x00, 0x03, 0x01, 0x83, 0x02];
                            await stream.WriteAsync(response, 0, response.Length, cts.Token);
                        }
                        catch (OperationCanceledException)
                        {
                            // 本连接超时，转到下一个连接
                        }
                        catch (Exception ex) when (ex is IOException or ObjectDisposedException)
                        {
                            // 客户端放弃连接引发的 IO 异常，转到下一个连接
                        }
                    }
                }
            });

            using var tcp = new ModbusTcpClient(new ModbusTcpClientConfig
            {
                Address = "127.0.0.1",
                ReadTimeout = 1000,
                WriteTimeout = 1000,
                // 并发负载下（如工厂并发冒烟测试阻塞线程池），连接等待同样可能超支 1s 预算，放宽以覆盖调度延迟。
                ConnectTimeout = 10000,
                Reconnect = true,
                RetryCount = 3,
                RetryInterval = 10
            });
            tcp.Config.Port = port;
            Assert.True(tcp.Connect());

            var result = tcp.Request(new ModbusRequest
            {
                SlaveId = 1,
                FunctionCode = ModbusFunctionCode.ReadHoldingRegisters,
                StartAddress = 0,
                Quantity = 1
            });

            listener.Stop();
            try { await server.WaitAsync(TimeSpan.FromSeconds(15)); }
            catch (TimeoutException) { /* Stop 后 pending 的 Accept 可能永不完成（.NET 8 已知行为），断言不依赖其退出 */ }

            // 异常响应是终态：立即失败并携带异常码，绝不重发请求
            Assert.False(result.IsSuccess);
            Assert.Contains("0x02", result.ErrorMessage);
            Assert.Equal(1, requestsReceived);
        }

        [Fact]
        public async Task TcpClient_InvalidPduLength_ReturnsFailureInsteadOfThrowing()
        {
            var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            var port = ((IPEndPoint)listener.LocalEndpoint).Port;

            var server = Task.Run(async () =>
            {
                // 接受最多 2 个连接，防御意外的重连行为
                for (int i = 0; i < 2; i++)
                {
                    TcpClient handler;
                    try
                    {
                        handler = await listener.AcceptTcpClientAsync();
                    }
                    // listener.Stop() 会使挂起的 Accept 抛 ObjectDisposedException；
                    // 若 Stop 先于本轮 Accept 开始，则抛 InvalidOperationException（Not listening）
                    catch (Exception ex) when (ex is ObjectDisposedException or InvalidOperationException or IOException or SocketException)
                    {
                        break; // listener 已停止，正常退出
                    }

                    using (handler)
                    using (var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10)))
                    {
                        try
                        {
                            var stream = handler.GetStream();
                            var request = new byte[12];
                            int read = 0;
                            while (read < request.Length)
                            {
                                int n = await stream.ReadAsync(request, read, request.Length - read, cts.Token);
                                if (n == 0)
                                    break;
                                read += n;
                            }

                            if (read < request.Length)
                                continue;

                            // MBAP length 字段 = 0：协议违规
                            byte[] resp = [0x00, 0x00, 0x00, 0x00, 0x00, 0x00];
                            await stream.WriteAsync(resp, 0, resp.Length, cts.Token);
                            await Task.Delay(200); // 给客户端留出读取时间
                        }
                        catch (OperationCanceledException)
                        {
                            // 本连接超时，转到下一个连接
                        }
                        catch (Exception ex) when (ex is IOException or ObjectDisposedException)
                        {
                            // 客户端放弃连接引发的 IO 异常，转到下一个连接
                        }
                    }
                }
            });

            using var tcp = new ModbusTcpClient(new ModbusTcpClientConfig
            {
                Address = "127.0.0.1",
                ReadTimeout = 1000,
                WriteTimeout = 1000,
                ConnectTimeout = 1000,
                Reconnect = true,
                RetryCount = 0   // 单次尝试，保证最终错误消息就是"PDU length"而不是后续超时
            });
            tcp.Config.Port = port;
            Assert.True(tcp.Connect());

            ModbusResult<byte[]> result = null!;
            var ex = await Record.ExceptionAsync(() =>
            {
                result = tcp.Request(new ModbusRequest
                {
                    SlaveId = 1,
                    FunctionCode = ModbusFunctionCode.ReadHoldingRegisters,
                    StartAddress = 0,
                    Quantity = 1
                });
                return Task.CompletedTask;
            });
            listener.Stop();
            try { await server.WaitAsync(TimeSpan.FromSeconds(15)); }
            catch (TimeoutException) { /* Stop 后 pending 的 Accept 可能永不完成（.NET 8 已知行为），断言不依赖其退出 */ }

            Assert.Null(ex);                       // 修复前：这里会捕获到 ModbusException
            Assert.False(result.IsSuccess);
            Assert.Contains("PDU length", result.ErrorMessage);
        }

        [Fact]
        public async Task TcpClient_ConcurrentConnectAndRequest_DoNotInterleave()
        {
            var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            var port = ((IPEndPoint)listener.LocalEndpoint).Port;

            var server = Task.Run(async () =>
            {
                // 接受最多 10 个连接：8 个并发任务中的 Connect/内部重连都可能建立新连接。
                for (int i = 0; i < 10; i++)
                {
                    TcpClient handler;
                    try
                    {
                        handler = await listener.AcceptTcpClientAsync();
                    }
                    catch (Exception ex) when (ex is ObjectDisposedException or InvalidOperationException
                        or IOException or SocketException)
                    {
                        break; // listener.Stop() 会使挂起/后续的 Accept 抛异常，正常退出
                    }

                    using (handler)
                    using (var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10)))
                    {
                        var stream = handler.GetStream();
                        var buffer = new byte[256];
                        try
                        {
                            // 保持连接打开并丢弃收到的数据（不做协议响应，客户端侧只验证无未归类异常）；
                            // 对端关闭或 10s 超时后转到下一个连接。
                            while (await stream.ReadAsync(buffer, 0, buffer.Length, cts.Token) > 0)
                            {
                            }
                        }
                        catch (OperationCanceledException)
                        {
                            // 本连接超时，转到下一个连接
                        }
                        catch (Exception ex) when (ex is IOException or ObjectDisposedException
                            or InvalidOperationException or SocketException)
                        {
                            // 客户端放弃连接引发的 IO 异常，转到下一个连接
                        }
                    }
                }
            });

            using (var tcp = new ModbusTcpClient(new ModbusTcpClientConfig
            {
                Address = "127.0.0.1",
                ReadTimeout = 200,
                WriteTimeout = 200,
                ConnectTimeout = 500
            }))
            {
                tcp.Config.Port = port;   // Task 2.4 之后 Port 为可写属性

                var tasks = Enumerable.Range(0, 8).Select(i => Task.Run(async () =>
                {
                    var ex = await Record.ExceptionAsync(() =>
                    {
                        if (i % 2 == 0)
                        {
                            tcp.Connect();
                            return Task.CompletedTask;
                        }
                        return tcp.RequestAsync(new ModbusRequest
                        {
                            SlaveId = 1,
                            FunctionCode = ModbusFunctionCode.ReadHoldingRegisters,
                            StartAddress = 0,
                            Quantity = 1
                        });
                    });
                    // 修复前：ResetSocket 可能在发送/读取中销毁 socket，抛出未归类的异常
                    Assert.True(ex is null or IOException or SocketException or TimeoutException
                        or OperationCanceledException or ModbusException,
                        $"Unexpected exception: {ex}");
                })).ToArray();

                await Task.WhenAll(tasks);
            }
            // 先释放客户端再等待服务器：关闭连接让服务器的下一次 ReadAsync 立即返回 0 并退出，
            // 避免服务器在 10s CTS 上空等造成测试尾部停顿。
            listener.Stop();
            try { await server.WaitAsync(TimeSpan.FromSeconds(15)); }
            catch (TimeoutException) { /* Stop 后 pending 的 Accept 可能永不完成（.NET 8 已知行为），断言不依赖其退出 */ }
        }

        [Fact]
        public void ModbusResult_Fail_DefaultsToUnspecifiedKind()
        {
            var result = ModbusResult<byte[]>.Fail("boom");
            Assert.Equal(ModbusErrorKind.Unspecified, result.ErrorKind);
        }

        [Fact]
        public void ModbusResult_Fail_CanCarryKind()
        {
            var result = ModbusResult<byte[]>.Fail("timeout", ModbusErrorKind.Timeout);
            Assert.Equal(ModbusErrorKind.Timeout, result.ErrorKind);
        }

        [Fact]
        public void ExceptionCode_Describe_ReturnsKnownText()
        {
            Assert.Equal("Illegal data address", ((ModbusExceptionCode)0x02).Describe());
            Assert.Contains("Unknown", ((ModbusExceptionCode)0x77).Describe());
        }

        [Fact]
        public async Task TcpClient_ReadTimeout_IsClassifiedAsTimeout()
        {
            // 监听但不回复 → 客户端读超时
            var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            var port = ((IPEndPoint)listener.LocalEndpoint).Port;
            using var tcp = new ModbusTcpClient(new ModbusTcpClientConfig
            {
                Address = "127.0.0.1",
                ReadTimeout = 300,
                WriteTimeout = 300,
                // 并发负载下（如工厂并发冒烟测试阻塞线程池），连接等待可能超支 1s 预算，
                // 放宽以覆盖调度延迟（与其他 TCP 测试一致）；超时分类仍由 ReadTimeout=300 决定。
                ConnectTimeout = 10000,
                RetryCount = 0   // 单次尝试：否则重连失败后最终 ErrorKind 会变成 ConnectionClosed 而非 Timeout
            });
            tcp.Config.Port = port;
            Assert.True(tcp.Connect());

            var result = tcp.Request(new ModbusRequest
            {
                SlaveId = 1,
                FunctionCode = ModbusFunctionCode.ReadHoldingRegisters,
                StartAddress = 0,
                Quantity = 1
            });
            listener.Stop();

            Assert.False(result.IsSuccess);
            Assert.Equal(ModbusErrorKind.Timeout, result.ErrorKind);
        }

        [Fact]
        public async Task TcpClient_RetryCount_RetriesThenSucceeds()
        {
            // 重试次数语义基线（Task 4.3）：RetryCount 为首次失败后的重试次数；
            // 第一次尝试读超时后客户端销毁连接并重连（新连接），服务器必须逐个 accept。
            var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            var port = ((IPEndPoint)listener.LocalEndpoint).Port;
            var requestCount = 0;
            var server = Task.Run(async () =>
            {
                // 循环 accept：第一次尝试超时后客户端会销毁连接并重连（新连接），必须逐个接受
                while (true)
                {
                    TcpClient client;
                    try { client = await listener.AcceptTcpClientAsync(); }
                    catch { break; }   // listener.Stop() 后退出
                    _ = HandleClientAsync(client);
                }
            });
            async Task HandleClientAsync(TcpClient client)
            {
                using var _ = client;
                using var opCts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                try
                {
                    var stream = client.GetStream();
                    var req = new byte[12];
                    int read = 0;
                    while (read < req.Length)
                        read += await stream.ReadAsync(req, read, req.Length - read, opCts.Token);
                    Interlocked.Increment(ref requestCount);
                    if (Volatile.Read(ref requestCount) == 1)
                        return; // 第一次不回复，制造超时
                    byte[] resp = [req[0], req[1], 0x00, 0x00, 0x00, 0x05, 0x01, 0x03, 0x02, 0x12, 0x34];
                    await stream.WriteAsync(resp, 0, resp.Length, opCts.Token);
                }
                catch
                {
                    // 客户端提前弃连/取消/Stop 中止 —— 服务器脚手架优雅退出
                }
            }

            using var tcp = new ModbusTcpClient(new ModbusTcpClientConfig
            {
                Address = "127.0.0.1",
                ReadTimeout = 1000,          // 首次超时由服务器"第 1 个请求不应答"保证，不依赖短预算；1s 给响应留足负载余量
                WriteTimeout = 1000,
                ConnectTimeout = 10000,      // 并行负载下 1s 会误报（与同文件既有测试一致）
                Reconnect = true,
                RetryCount = 2,
                RetryInterval = 10
            });
            tcp.Config.Port = port;

            var result = tcp.Request(new ModbusRequest
            {
                SlaveId = 1,
                FunctionCode = ModbusFunctionCode.ReadHoldingRegisters,
                StartAddress = 0,
                Quantity = 1
            });
            listener.Stop();
            try { await server.WaitAsync(TimeSpan.FromSeconds(15)); }
            catch (TimeoutException) { /* Stop 后 pending 的 Accept 可能永不完成（.NET 8 已知行为），断言不依赖其退出 */ }

            Assert.True(result.IsSuccess, result.ErrorMessage);
            // 首次必然超时（服务器不应答）→ 至少 2 次交换；重试预算 RetryCount+1=3 次尝试 → 至多 3 次
            // （并行负载下第 2 次尝试也可能超时后由第 3 次成功）。
            Assert.InRange(Volatile.Read(ref requestCount), 2, 3);
        }

        [Fact]
        public async Task TcpClient_RequestAsync_CancelledDuringRead_ReturnsCancelledQuickly()
        {
            // 监听但不回复 → 客户端读挂起 → 500ms 后取消令牌触发，应尽快返回 Cancelled
            var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            var port = ((IPEndPoint)listener.LocalEndpoint).Port;
            _ = Task.Run(async () =>
            {
                try
                {
                    using var client = await listener.AcceptTcpClientAsync();
                    // 接受连接后保持沉默
                    await Task.Delay(5000);
                }
                catch
                {
                    // listener.Stop() 中止 accept
                }
            });

            using var tcp = new ModbusTcpClient(new ModbusTcpClientConfig
            {
                Address = "127.0.0.1",
                ReadTimeout = 30_000,        // 故意大于取消时间
                WriteTimeout = 30_000,
                ConnectTimeout = 10000,      // 并行负载下 1s 会误报（与同文件既有测试一致）
                RetryCount = 0
            });
            tcp.Config.Port = port;
            Assert.True(tcp.Connect());

            using var cts = new CancellationTokenSource(500);
            var sw = Stopwatch.StartNew();
            var result = await tcp.RequestAsync(new ModbusRequest
            {
                SlaveId = 1,
                FunctionCode = ModbusFunctionCode.ReadHoldingRegisters,
                StartAddress = 0,
                Quantity = 1
            }, cts.Token);
            sw.Stop();
            listener.Stop();

            Assert.False(result.IsSuccess);
            Assert.Equal(ModbusErrorKind.Cancelled, result.ErrorKind);
            Assert.True(sw.ElapsedMilliseconds < 5000, $"Cancellation took {sw.ElapsedMilliseconds}ms");
        }
    }
}
