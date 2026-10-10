using Junevy.Communication.Modbus.Core.Interfaces;
using Junevy.Communication.Modbus.Core.Models;
using Junevy.Communication.Modbus.Extensions;
using Moq;

namespace Junevy.Communication.Modbus.Tests
{
    /// <summary>
    /// 扩展方法构造的 ModbusRequest 快照：重构 ModbusExtensions 时，任何功能码、起始地址、
    /// 数量或 Data 打包的变化都会被这里拦住（协议线上行为不能被重构悄悄改变）。
    /// ProtocolType 返回 RTU，使 PDU 偏移为 0，便于统一断言。
    /// </summary>
    public class ExtensionRequestTests
    {
        public static IEnumerable<object[]> RequestShapes()
        {
            // 调用, FunctionCode, StartAddress, Quantity, Data(hex 或 null)
            yield return new object[] { "ReadCoils", typeof(bool[]), ModbusFunctionCode.ReadCoils, (ushort)10, (ushort)16, (string)null };
            yield return new object[] { "ReadDiscreteInputs", typeof(bool[]), ModbusFunctionCode.ReadDiscreteInputs, (ushort)10, (ushort)16, (string)null };
            yield return new object[] { "ReadHoldingRegisters", typeof(ushort[]), ModbusFunctionCode.ReadHoldingRegisters, (ushort)10, (ushort)4, (string)null };
            yield return new object[] { "ReadInputRegisters", typeof(ushort[]), ModbusFunctionCode.ReadInputRegisters, (ushort)10, (ushort)4, (string)null };
            yield return new object[] { "WriteSingleCoilTrue", typeof(byte[]), ModbusFunctionCode.WriteCoil, (ushort)10, (ushort)1, "FF00" };
            yield return new object[] { "WriteSingleCoilFalse", typeof(byte[]), ModbusFunctionCode.WriteCoil, (ushort)10, (ushort)1, "0000" };
            yield return new object[] { "WriteSingleRegister", typeof(byte[]), ModbusFunctionCode.WriteHoldingRegister, (ushort)10, (ushort)1, "1234" };
            yield return new object[] { "WriteMultipleCoils", typeof(byte[]), ModbusFunctionCode.WriteMultipleCoils, (ushort)10, (ushort)9, "0D01" };
            yield return new object[] { "WriteMultipleRegisters", typeof(byte[]), ModbusFunctionCode.WriteMultipleHoldingRegisters, (ushort)10, (ushort)2, "00010203" };
            yield return new object[] { "ReadExceptionStatus", typeof(byte), ModbusFunctionCode.ReadExceptionStatus, (ushort)0, (ushort)0, (string)null };
            yield return new object[] { "DiagnosticsUshort", typeof(byte[]), ModbusFunctionCode.Diagnostics, (ushort)0, (ushort)0, "00001234" };
            yield return new object[] { "DiagnosticsBytes", typeof(byte[]), ModbusFunctionCode.Diagnostics, (ushort)0, (ushort)0, "0001010203" };
            yield return new object[] { "GetCommEventCounter", typeof(ModbusCommEventCounter), ModbusFunctionCode.GetCommEventCounter, (ushort)0, (ushort)0, (string)null };
            yield return new object[] { "GetCommEventLog", typeof(ModbusCommEventLog), ModbusFunctionCode.GetCommEventLog, (ushort)0, (ushort)0, (string)null };
            yield return new object[] { "ReportServerId", typeof(byte[]), ModbusFunctionCode.ReportServerId, (ushort)0, (ushort)0, (string)null };
            yield return new object[] { "MaskWriteRegister", typeof(byte[]), ModbusFunctionCode.MaskWriteRegister, (ushort)10, (ushort)1, "F2F22525" };
            yield return new object[] { "ReadWriteMultipleRegisters", typeof(ushort[]), ModbusFunctionCode.ReadWriteMultipleRegisters, (ushort)3, (ushort)6, "00030006000E00030600FF00FF00FF" };
        }

        [Theory]
        [MemberData(nameof(RequestShapes))]
        public void SyncCall_BuildsExpectedRequest(
            string operation,
            Type resultType,
            ModbusFunctionCode functionCode,
            ushort start,
            ushort quantity,
            string expectedData)
        {
            var (modbus, captured) = CreateModbus(resultType);
            InvokeSync(modbus, operation);

            AssertRequest(captured, functionCode, start, quantity, expectedData, operation);
        }

        [Theory]
        [MemberData(nameof(RequestShapes))]
        public async Task AsyncCall_BuildsExpectedRequest(
            string operation,
            Type resultType,
            ModbusFunctionCode functionCode,
            ushort start,
            ushort quantity,
            string expectedData)
        {
            var (modbus, captured) = CreateModbus(resultType);
            await InvokeAsync(modbus, operation);

            AssertRequest(captured, functionCode, start, quantity, expectedData, operation);
        }

        public static IEnumerable<object[]> InvalidArguments()
        {
            yield return new object[] { "ReadHoldingRegistersZero", 0 };
            yield return new object[] { "ReadHoldingRegistersTooMany", 126 };
            yield return new object[] { "ReadCoilsTooMany", 2001 };
            yield return new object[] { "WriteMultipleRegistersEmpty", -1 };
            yield return new object[] { "WriteMultipleCoilsTooMany", -2 };
        }

        [Theory]
        [MemberData(nameof(InvalidArguments))]
        public void SyncCall_InvalidArguments_ThrowsSynchronously(string operation, int marker)
        {
            var (modbus, _) = CreateModbus(typeof(ushort[]));

            // 异步版本必须"在调用点同步抛出"，而不是返回一个已故障的任务
            Assert.Throws<ArgumentException>(() => InvokeSync(modbus, operation, marker));
            Assert.Throws<ArgumentException>(() => InvokeAsync(modbus, operation, marker).GetAwaiter().GetResult());
        }

        // ————————————————— 私有辅助 —————————————————

        private static (IModbus modbus, List<ModbusRequest> captured) CreateModbus(Type resultType)
        {
            // 用 List 作为持有者：回调写入的是同一个实例，返回值才看得到
            var captured = new List<ModbusRequest>();
            var modbus = new Mock<IModbus>();
            modbus.Setup(m => m.ProtocolType).Returns(ModbusProtocolType.RTU);
            modbus
                .Setup(m => m.Request(It.IsAny<ModbusRequest>()))
                .Callback<ModbusRequest>(r => captured.Add(r))
                .Returns(CreateSuccessPayload(resultType));
            modbus
                .Setup(m => m.RequestAsync(It.IsAny<ModbusRequest>(), It.IsAny<CancellationToken>()))
                .Callback<ModbusRequest, CancellationToken>((r, _) => captured.Add(r))
                .ReturnsAsync(CreateSuccessPayload(resultType));

            return (modbus.Object, captured);
        }

        /// <summary>构造一个能让对应高层 API 解析成功的响应负载（Data 已被剥离到 PDU 层）。</summary>
        private static ModbusResult<byte[]> CreateSuccessPayload(Type resultType)
        {
            if (resultType == typeof(ushort[]))
                return ModbusResult<byte[]>.Success(new byte[] { 0x03, 0x02, 0x00, 0x01, 0x00, 0x02 });

            if (resultType == typeof(bool[]))
                return ModbusResult<byte[]>.Success(new byte[] { 0x01, 0x01, 0x01 });

            if (resultType == typeof(ModbusCommEventCounter))
                return ModbusResult<byte[]>.Success(new byte[] { 0x0B, 0x00, 0x00, 0x00, 0x08 });

            if (resultType == typeof(ModbusCommEventLog))
                return ModbusResult<byte[]>.Success(new byte[] { 0x0C, 0x06, 0x00, 0x00, 0x00, 0x08, 0x00, 0x0A, 0x01 });

            // byte[] 与 byte：写类回显 / 0x07 / 0x08 / 0x11
            return ModbusResult<byte[]>.Success(new byte[] { 0x11, 0x02, 0x00, 0x01, 0xFF });
        }

        private static void AssertRequest(
            List<ModbusRequest> captured,
            ModbusFunctionCode functionCode,
            ushort start,
            ushort quantity,
            string expectedData,
            string operation)
        {
            Assert.True(captured.Count == 1, $"expected exactly 1 captured request, got {captured.Count} ({operation})");
            var request = captured[0];
            Assert.Equal((byte)1, request.SlaveId);
            Assert.Equal(functionCode, request.FunctionCode);
            Assert.Equal(start, request.StartAddress);
            Assert.Equal(quantity, request.Quantity);
            Assert.Equal(expectedData, request.Data == null ? null : Convert.ToHexString(request.Data));
        }

        private static void InvokeSync(IModbus modbus, string operation, int marker = 0)
        {
            switch (operation)
            {
                case "ReadCoils": modbus.ReadCoils(1, 10, 16); break;
                case "ReadDiscreteInputs": modbus.ReadDiscreteInputs(1, 10, 16); break;
                case "ReadHoldingRegisters": modbus.ReadHoldingRegisters(1, 10, 4); break;
                case "ReadInputRegisters": modbus.ReadInputRegisters(1, 10, 4); break;
                case "WriteSingleCoilTrue": modbus.WriteSingleCoil(1, 10, true); break;
                case "WriteSingleCoilFalse": modbus.WriteSingleCoil(1, 10, false); break;
                case "WriteSingleRegister": modbus.WriteSingleRegister(1, 10, 0x1234); break;
                case "WriteMultipleCoils": modbus.WriteMultipleCoils(1, 10, Coils()); break;
                case "WriteMultipleRegisters": modbus.WriteMultipleRegisters(1, 10, new ushort[] { 0x0001, 0x0203 }); break;
                case "ReadExceptionStatus": modbus.ReadExceptionStatus(1); break;
                case "DiagnosticsUshort": modbus.Diagnostics(1, 0x0000, (ushort)0x1234); break;
                case "DiagnosticsBytes": modbus.Diagnostics(1, 0x0001, new byte[] { 1, 2, 3 }); break;
                case "GetCommEventCounter": modbus.GetCommEventCounter(1); break;
                case "GetCommEventLog": modbus.GetCommEventLog(1); break;
                case "ReportServerId": modbus.ReportServerId(1); break;
                case "MaskWriteRegister": modbus.MaskWriteRegister(1, 10, 0xF2F2, 0x2525); break;
                case "ReadWriteMultipleRegisters": modbus.ReadWriteMultipleRegisters(1, 3, 6, 14, new ushort[] { 0x00FF, 0x00FF, 0x00FF }); break;

                // 参数校验用例：marker 选择非法参数
                case "ReadHoldingRegistersZero": modbus.ReadHoldingRegisters(1, 0, 0); break;
                case "ReadHoldingRegistersTooMany": modbus.ReadHoldingRegisters(1, 0, 126); break;
                case "ReadCoilsTooMany": modbus.ReadCoils(1, 0, 2001); break;
                case "WriteMultipleRegistersEmpty": modbus.WriteMultipleRegisters(1, 0, Array.Empty<ushort>()); break;
                case "WriteMultipleCoilsTooMany": modbus.WriteMultipleCoils(1, 0, Coils(1969)); break;
                default: throw new ArgumentOutOfRangeException(nameof(operation), operation, "unknown operation");
            }
        }

        private static Task InvokeAsync(IModbus modbus, string operation, int marker = 0)
        {
            switch (operation)
            {
                case "ReadCoils": return modbus.ReadCoilsAsync(1, 10, 16).AsTask();
                case "ReadDiscreteInputs": return modbus.ReadDiscreteInputsAsync(1, 10, 16).AsTask();
                case "ReadHoldingRegisters": return modbus.ReadHoldingRegistersAsync(1, 10, 4).AsTask();
                case "ReadInputRegisters": return modbus.ReadInputRegistersAsync(1, 10, 4).AsTask();
                case "WriteSingleCoilTrue": return modbus.WriteSingleCoilAsync(1, 10, true).AsTask();
                case "WriteSingleCoilFalse": return modbus.WriteSingleCoilAsync(1, 10, false).AsTask();
                case "WriteSingleRegister": return modbus.WriteSingleRegisterAsync(1, 10, 0x1234).AsTask();
                case "WriteMultipleCoils": return modbus.WriteMultipleCoilsAsync(1, 10, Coils()).AsTask();
                case "WriteMultipleRegisters": return modbus.WriteMultipleRegistersAsync(1, 10, new ushort[] { 0x0001, 0x0203 }).AsTask();
                case "ReadExceptionStatus": return modbus.ReadExceptionStatusAsync(1).AsTask();
                case "DiagnosticsUshort": return modbus.DiagnosticsAsync(1, 0x0000, (ushort)0x1234).AsTask();
                case "DiagnosticsBytes": return modbus.DiagnosticsAsync(1, 0x0001, new byte[] { 1, 2, 3 }).AsTask();
                case "GetCommEventCounter": return modbus.GetCommEventCounterAsync(1).AsTask();
                case "GetCommEventLog": return modbus.GetCommEventLogAsync(1).AsTask();
                case "ReportServerId": return modbus.ReportServerIdAsync(1).AsTask();
                case "MaskWriteRegister": return modbus.MaskWriteRegisterAsync(1, 10, 0xF2F2, 0x2525).AsTask();
                case "ReadWriteMultipleRegisters": return modbus.ReadWriteMultipleRegistersAsync(1, 3, 6, 14, new ushort[] { 0x00FF, 0x00FF, 0x00FF }).AsTask();

                case "ReadHoldingRegistersZero": return modbus.ReadHoldingRegistersAsync(1, 0, 0).AsTask();
                case "ReadHoldingRegistersTooMany": return modbus.ReadHoldingRegistersAsync(1, 0, 126).AsTask();
                case "ReadCoilsTooMany": return modbus.ReadCoilsAsync(1, 0, 2001).AsTask();
                case "WriteMultipleRegistersEmpty": return modbus.WriteMultipleRegistersAsync(1, 0, Array.Empty<ushort>()).AsTask();
                case "WriteMultipleCoilsTooMany": return modbus.WriteMultipleCoilsAsync(1, 0, Coils(1969)).AsTask();
                default: throw new ArgumentOutOfRangeException(nameof(operation), operation, "unknown operation");
            }
        }

        private static bool[] Coils(int length = 9)
        {
            // 计划 3.2 指定的向量：T,F,T,T,F,F,F,F,T（true 在下标 0/2/3/8 → 字节 0x0D, 0x01）
            bool[] values = new bool[length];
            if (length == 9)
            {
                values[0] = true;
                values[2] = true;
                values[3] = true;
                values[8] = true;
                return values;
            }

            for (int i = 0; i < length; i++)
                values[i] = i % 2 == 0;
            return values;
        }
    }
}