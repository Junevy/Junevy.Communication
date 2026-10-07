using Junevy.Communication.Modbus.Core.Interfaces;
using Junevy.Communication.Modbus.Core.Models;
using Junevy.Communication.Modbus.Extensions;
using Junevy.Communication.Modbus.Tests.TestSupport;
using Junevy.Communication.Modbus.Utils;
using Moq;
using System.Net.Sockets;

namespace Junevy.Communication.Modbus.Tests
{
    /// <summary>
    /// 高层 API（ModbusExtensions）错误分类验证：
    /// 1) 底层失败时保留 ErrorKind（此前一律重置为 Unspecified）；
    /// 2) 底层"成功"但数据过短时返回 ProtocolViolation，不抛 ArgumentException（结果式 API 不抛异常）；
    /// 3) 传输层通信异常按类型归类为 Timeout / ConnectionClosed。
    /// </summary>
    public class ModbusExtensionsErrorKindTests
    {
        public static IEnumerable<object[]> PropagationCases()
        {
            var ops = new[]
            {
                "ReadCoils", "ReadDiscreteInputs", "ReadHoldingRegisters", "ReadInputRegisters",
                "WriteSingleCoil", "WriteSingleRegister", "WriteMultipleCoils", "WriteMultipleRegisters",
                "ReadExceptionStatus", "DiagnosticsUshort", "DiagnosticsBytes",
                "GetCommEventCounter", "GetCommEventLog", "ReportServerId",
                "MaskWriteRegister", "ReadWriteMultipleRegisters"
            };
            var kinds = new[]
            {
                ModbusErrorKind.Timeout, ModbusErrorKind.ConnectionClosed, ModbusErrorKind.Cancelled,
                ModbusErrorKind.ModbusException, ModbusErrorKind.ProtocolViolation, ModbusErrorKind.InvalidRequest
            };

            foreach (var op in ops)
                foreach (var isAsync in new[] { false, true })
                    foreach (var kind in kinds)
                        yield return new object[] { op, isAsync, kind };
        }

        [Theory]
        [MemberData(nameof(PropagationCases))]
        public async Task HighLevelApi_PropagatesErrorKind(string op, bool isAsync, ModbusErrorKind kind)
        {
            var mock = CreateMock();
            mock.Setup(m => m.Request(It.IsAny<ModbusRequest>()))
                .Returns(ModbusResult<byte[]>.Fail("x", kind));
            mock.Setup(m => m.RequestAsync(It.IsAny<ModbusRequest>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(ModbusResult<byte[]>.Fail("x", kind));

            var (isSuccess, errorKind) = await InvokeAsync(mock.Object, op, isAsync);

            Assert.False(isSuccess);
            Assert.Equal(kind, errorKind);
        }

        [Fact]
        public async Task HighLevelApi_ShortData_ReturnsProtocolViolationWithoutThrowing()
        {
            // RTU 偏移 0："PDU"= [addr, FC, ...]。[1,3,2] 对读功能码是截断响应
            // （声明 byteCount=2 但无 payload），对 GetCommEvent*/ReportServerId 长度不足。
            var mock = CreateMock();
            var shortResponse = ModbusResult<byte[]>.Success(new byte[] { 1, 3, 2 });
            mock.Setup(m => m.Request(It.IsAny<ModbusRequest>())).Returns(shortResponse);
            mock.Setup(m => m.RequestAsync(It.IsAny<ModbusRequest>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(shortResponse);

            var modbus = mock.Object;

            foreach (var result in new object[]
                     {
                         await modbus.ReadHoldingRegistersAsync(1, 0, 10),
                         modbus.ReadHoldingRegisters(1, 0, 10),
                         await modbus.ReadCoilsAsync(1, 0, 16),
                         modbus.ReadCoils(1, 0, 16),
                         await modbus.ReadWriteMultipleRegistersAsync(1, 0, 10, 0, new ushort[] { 1 }),
                         modbus.ReadWriteMultipleRegisters(1, 0, 10, 0, new ushort[] { 1 }),
                         await modbus.GetCommEventCounterAsync(1),
                         modbus.GetCommEventCounter(1),
                         await modbus.GetCommEventLogAsync(1),
                         modbus.GetCommEventLog(1),
                         await modbus.ReportServerIdAsync(1),
                         modbus.ReportServerId(1)
                     })
            {
                dynamic r = result;
                Assert.False(r.IsSuccess);
                Assert.Equal(ModbusErrorKind.ProtocolViolation, r.ErrorKind);
            }
        }

        [Fact]
        public async Task ReadExceptionStatus_CompleteShortResponse_ParsesWithoutThrowing()
        {
            // 裁定（见 ledger）：[1,3,2] 在 RTU 偏移 0 约定下是完整的 ReadExceptionStatus 响应
            // （addr, FC, status）——拒绝它需要响应功能码校验（审查第 8 条，计划三）。
            // 本用例固化现状：不抛异常、正常解析 status。
            var mock = CreateMock();
            var completeResponse = ModbusResult<byte[]>.Success(new byte[] { 1, 3, 2 });
            mock.Setup(m => m.Request(It.IsAny<ModbusRequest>())).Returns(completeResponse);
            mock.Setup(m => m.RequestAsync(It.IsAny<ModbusRequest>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(completeResponse);

            var syncResult = mock.Object.ReadExceptionStatus(1);
            var asyncResult = await mock.Object.ReadExceptionStatusAsync(1);

            Assert.True(syncResult.IsSuccess);
            Assert.Equal((byte)2, syncResult.Data);
            Assert.True(asyncResult.IsSuccess);
            Assert.Equal((byte)2, asyncResult.Data);
        }

        [Fact]
        public void ParseCoils_ShortByOne_ThrowsArgumentException()
        {
            // 长度 3，需要 3 + 1 = 4：此前检查 2 + 1 = 3 放行了越界读取（IndexOutOfRangeException）
            Assert.Throws<ArgumentException>(() => ModbusHelper.ParseCoils(new byte[] { 1, 1, 1 }, 8));
        }

        [Fact]
        public void Transport_SocketException_IsClassifiedAsConnectionClosed()
        {
            var transport = new FakeTransport(reconnectEnabled: false, retryCount: 0, retryInterval: 0)
            {
                OnSendFrame = () => throw new SocketException()
            };

            var result = transport.Request(new ModbusRequest
            {
                SlaveId = 1,
                FunctionCode = ModbusFunctionCode.ReadHoldingRegisters,
                StartAddress = 0,
                Quantity = 1
            });

            Assert.False(result.IsSuccess);
            Assert.Equal(ModbusErrorKind.ConnectionClosed, result.ErrorKind);
        }

        [Fact]
        public void Transport_TimeoutException_IsClassifiedAsTimeout()
        {
            var transport = new FakeTransport(reconnectEnabled: false, retryCount: 0, retryInterval: 0)
            {
                OnSendFrame = () => throw new TimeoutException()
            };

            var result = transport.Request(new ModbusRequest
            {
                SlaveId = 1,
                FunctionCode = ModbusFunctionCode.ReadHoldingRegisters,
                StartAddress = 0,
                Quantity = 1
            });

            Assert.False(result.IsSuccess);
            Assert.Equal(ModbusErrorKind.Timeout, result.ErrorKind);
        }

        // ————————————————— 私有辅助 —————————————————

        private static Mock<IModbus> CreateMock()
        {
            var mock = new Mock<IModbus>();
            mock.Setup(m => m.ProtocolType).Returns(ModbusProtocolType.RTU);
            return mock;
        }

        private static async Task<(bool IsSuccess, ModbusErrorKind ErrorKind)> InvokeAsync(
            IModbus modbus, string op, bool isAsync)
        {
            switch (op)
            {
                case "ReadCoils":
                    if (isAsync) { var r = await modbus.ReadCoilsAsync(1, 0, 1); return (r.IsSuccess, r.ErrorKind); }
                    { var r = modbus.ReadCoils(1, 0, 1); return (r.IsSuccess, r.ErrorKind); }
                case "ReadDiscreteInputs":
                    if (isAsync) { var r = await modbus.ReadDiscreteInputsAsync(1, 0, 1); return (r.IsSuccess, r.ErrorKind); }
                    { var r = modbus.ReadDiscreteInputs(1, 0, 1); return (r.IsSuccess, r.ErrorKind); }
                case "ReadHoldingRegisters":
                    if (isAsync) { var r = await modbus.ReadHoldingRegistersAsync(1, 0, 1); return (r.IsSuccess, r.ErrorKind); }
                    { var r = modbus.ReadHoldingRegisters(1, 0, 1); return (r.IsSuccess, r.ErrorKind); }
                case "ReadInputRegisters":
                    if (isAsync) { var r = await modbus.ReadInputRegistersAsync(1, 0, 1); return (r.IsSuccess, r.ErrorKind); }
                    { var r = modbus.ReadInputRegisters(1, 0, 1); return (r.IsSuccess, r.ErrorKind); }
                case "WriteSingleCoil":
                    if (isAsync) { var r = await modbus.WriteSingleCoilAsync(1, 0, true); return (r.IsSuccess, r.ErrorKind); }
                    { var r = modbus.WriteSingleCoil(1, 0, true); return (r.IsSuccess, r.ErrorKind); }
                case "WriteSingleRegister":
                    if (isAsync) { var r = await modbus.WriteSingleRegisterAsync(1, 0, 1); return (r.IsSuccess, r.ErrorKind); }
                    { var r = modbus.WriteSingleRegister(1, 0, 1); return (r.IsSuccess, r.ErrorKind); }
                case "WriteMultipleCoils":
                    if (isAsync) { var r = await modbus.WriteMultipleCoilsAsync(1, 0, new[] { true }); return (r.IsSuccess, r.ErrorKind); }
                    { var r = modbus.WriteMultipleCoils(1, 0, new[] { true }); return (r.IsSuccess, r.ErrorKind); }
                case "WriteMultipleRegisters":
                    if (isAsync) { var r = await modbus.WriteMultipleRegistersAsync(1, 0, new ushort[] { 1 }); return (r.IsSuccess, r.ErrorKind); }
                    { var r = modbus.WriteMultipleRegisters(1, 0, new ushort[] { 1 }); return (r.IsSuccess, r.ErrorKind); }
                case "ReadExceptionStatus":
                    if (isAsync) { var r = await modbus.ReadExceptionStatusAsync(1); return (r.IsSuccess, r.ErrorKind); }
                    { var r = modbus.ReadExceptionStatus(1); return (r.IsSuccess, r.ErrorKind); }
                case "DiagnosticsUshort":
                    if (isAsync) { var r = await modbus.DiagnosticsAsync(1, 0, (ushort)1); return (r.IsSuccess, r.ErrorKind); }
                    { var r = modbus.Diagnostics(1, 0, (ushort)1); return (r.IsSuccess, r.ErrorKind); }
                case "DiagnosticsBytes":
                    if (isAsync) { var r = await modbus.DiagnosticsAsync(1, 0, new byte[] { 1, 2, 3 }); return (r.IsSuccess, r.ErrorKind); }
                    { var r = modbus.Diagnostics(1, 0, new byte[] { 1, 2, 3 }); return (r.IsSuccess, r.ErrorKind); }
                case "GetCommEventCounter":
                    if (isAsync) { var r = await modbus.GetCommEventCounterAsync(1); return (r.IsSuccess, r.ErrorKind); }
                    { var r = modbus.GetCommEventCounter(1); return (r.IsSuccess, r.ErrorKind); }
                case "GetCommEventLog":
                    if (isAsync) { var r = await modbus.GetCommEventLogAsync(1); return (r.IsSuccess, r.ErrorKind); }
                    { var r = modbus.GetCommEventLog(1); return (r.IsSuccess, r.ErrorKind); }
                case "ReportServerId":
                    if (isAsync) { var r = await modbus.ReportServerIdAsync(1); return (r.IsSuccess, r.ErrorKind); }
                    { var r = modbus.ReportServerId(1); return (r.IsSuccess, r.ErrorKind); }
                case "MaskWriteRegister":
                    if (isAsync) { var r = await modbus.MaskWriteRegisterAsync(1, 0, 0x00FF, 0x0001); return (r.IsSuccess, r.ErrorKind); }
                    { var r = modbus.MaskWriteRegister(1, 0, 0x00FF, 0x0001); return (r.IsSuccess, r.ErrorKind); }
                case "ReadWriteMultipleRegisters":
                    if (isAsync) { var r = await modbus.ReadWriteMultipleRegistersAsync(1, 0, 1, 0, new ushort[] { 1 }); return (r.IsSuccess, r.ErrorKind); }
                    { var r = modbus.ReadWriteMultipleRegisters(1, 0, 1, 0, new ushort[] { 1 }); return (r.IsSuccess, r.ErrorKind); }
                default:
                    throw new ArgumentOutOfRangeException(nameof(op), op, "Unknown operation.");
            }
        }
    }
}
