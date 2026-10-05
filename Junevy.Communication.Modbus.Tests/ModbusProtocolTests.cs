using Junevy.Communication.Modbus.Core.Framing;
using Junevy.Communication.Modbus.Core.Models;
using Junevy.Communication.Modbus.Core.Parsing;
using Junevy.Communication.Modbus.RTU;
using Junevy.Communication.Modbus.TCP;
using Junevy.Communication.Modbus.Utils;
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
                Start = 0x1234,
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
                Start = 0x0010,
                Length = 2
            };
            var builder = new ModbusFrameBuilder();
            Span<byte> destination = stackalloc byte[ModbusFrameBuilder.MaxTcpAduLength];

            Assert.True(builder.TryWriteRequestFrame(request, destination, out int written));

            Assert.Equal(12, written);
            Assert.Equal([0x00, 0x00, 0x00, 0x00, 0x00, 0x06, 0x02, 0x03, 0x00, 0x10, 0x00, 0x02],
                destination[..written].ToArray());
        }

        [Fact]
        public void RtuParser_WriteMultiShortFrame_ReturnsFailureInsteadOfThrowing()
        {
            var parser = new RtuProtocolParser();
            var request = new ModbusRequest
            {
                SlaveId = 1,
                FunctionCode = ModbusFunctionCode.WriteMultipleHoldingRegisters,
                Start = 0x0010,
                Length = 2,
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
                Start = 0x0010,
                Length = 2,
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
                Start = 0x0010,
                Length = 2,
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
                Start = 0,
                Length = 1
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
                Start = 0x0010,
                Length = 2,
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
                Start = 0,
                Length = 1
            };
            byte[] response = [0x00, 0x00, 0x00, 0x00, 0x00, 0x03, 0x01, 0x83, 0x02];

            var result = new TcpProtocolParser().ParseResponse(response, request);

            Assert.False(result.IsSuccess);
            Assert.Contains("0x02", result.ErrorMessage);
            Assert.Equal(9, result.Data.Length);
        }

        [Fact]
        public void TcpConfig_SetPort_AllowsWellKnownModbusPort()
        {
            var config = new ModbusTCPConfig();

            Assert.True(config.SetPort(502));
            Assert.Equal(502, config.Port);
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
                Start = 0,
                Length = 1
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
                byte[] response =
                [
                    0x00, 0x00,
                    0x00, 0x00,
                    0x00, 0x05,
                    0x01,
                    0x03,
                    0x02,
                    0x12, 0x34
                ];

                using var reconnect = await listener.AcceptTcpClientAsync();
                using var stream = reconnect.GetStream();

                byte[] request = new byte[12];
                int read = 0;
                while (read < request.Length)
                    read += await stream.ReadAsync(request, read, request.Length - read);

                await stream.WriteAsync(response, 0, response.Length);
            });

            using var client = new ModbusTCP(new ModbusTCPConfig
            {
                Address = IPAddress.Loopback.ToString(),
                ReadTimeOut = 1000,
                WriteTimeOut = 1000,
                ConnectTimeout = 1000,
                Reconnect = true,
                RetryCount = 3,
                RetryInterval = 10
            });
            Assert.True(client.Config.SetPort(port));

            var result = client.Request(new ModbusRequest
            {
                SlaveId = 1,
                FunctionCode = ModbusFunctionCode.ReadHoldingRegisters,
                Start = 0,
                Length = 1
            });

            listener.Stop();
            await serverTask;

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
                Start = 0,
                Length = 1
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
                using var client = await listener.AcceptTcpClientAsync();
                var stream = client.GetStream();
                for (int i = 0; i < 2; i++)
                {
                    var req = new byte[12];
                    int read = 0;
                    while (read < req.Length)
                        read += await stream.ReadAsync(req, read, req.Length - read);
                    seenTids.Add(req[..2].ToArray());
                    byte[] resp = [req[0], req[1], 0x00, 0x00, 0x00, 0x05, 0x01, 0x03, 0x02, 0x12, 0x34];
                    await stream.WriteAsync(resp, 0, resp.Length);
                }
            });

            using var tcp = new ModbusTCP(new ModbusTCPConfig
            {
                Address = "127.0.0.1",
                ReadTimeOut = 1000,
                WriteTimeOut = 1000,
                ConnectTimeout = 1000,
                Reconnect = true
            });
            tcp.Config.SetPort(port);

            var request = new ModbusRequest { SlaveId = 1, FunctionCode = ModbusFunctionCode.ReadHoldingRegisters, Start = 0, Length = 1 };
            var r1 = tcp.Request(request);
            var r2 = tcp.Request(request);
            listener.Stop();
            await server;

            Assert.True(r1.IsSuccess, r1.ErrorMessage);
            Assert.True(r2.IsSuccess, r2.ErrorMessage);
            Assert.Equal(new byte[] { 0x00, 0x00 }, seenTids[0]);
            Assert.Equal(new byte[] { 0x00, 0x01 }, seenTids[1]);
        }
    }
}
