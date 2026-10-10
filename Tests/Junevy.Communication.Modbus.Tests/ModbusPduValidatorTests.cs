using Junevy.Communication.Modbus.Core.Models;
using Junevy.Communication.Modbus.Core.Parsing;

namespace Junevy.Communication.Modbus.Tests
{
    /// <summary>
    /// <see cref="ModbusPduValidator"/> 的长期规格测试：期望长度表与 Validate 的校验顺序。
    /// </summary>
    public class ModbusPduValidatorTests
    {
        private static readonly ModbusPduValidator Validator = new();

        // ————————————————— GetExpectedPduLength —————————————————

        [Theory]
        // 功能码, 前缀, 请求(功能码/数量/Data), 期望长度
        [InlineData(ModbusFunctionCode.ReadCoils, "01 02", 0, null, 4)]              // 2 + byteCount
        [InlineData(ModbusFunctionCode.ReadDiscreteInputs, "02 03", 0, null, 5)]
        [InlineData(ModbusFunctionCode.ReadHoldingRegisters, "03 04", 0, null, 6)]
        [InlineData(ModbusFunctionCode.ReadInputRegisters, "04 02", 0, null, 4)]
        [InlineData(ModbusFunctionCode.ReadWriteMultipleRegisters, "17 02", 0, null, 4)]
        [InlineData(ModbusFunctionCode.GetCommEventLog, "0C 06", 0, null, 8)]
        [InlineData(ModbusFunctionCode.ReportServerId, "11 03", 0, null, 5)]
        [InlineData(ModbusFunctionCode.WriteCoil, "05", 0, null, 5)]
        [InlineData(ModbusFunctionCode.WriteHoldingRegister, "06", 0, null, 5)]
        [InlineData(ModbusFunctionCode.WriteMultipleCoils, "0F", 0, null, 5)]
        [InlineData(ModbusFunctionCode.WriteMultipleHoldingRegisters, "10", 0, null, 5)]
        [InlineData(ModbusFunctionCode.MaskWriteRegister, "16", 0, null, 7)]
        [InlineData(ModbusFunctionCode.ReadExceptionStatus, "07", 0, null, 2)]
        [InlineData(ModbusFunctionCode.Diagnostics, "08", 0, "00 00 12 34", 5)]        // 1 + request.Data.Length
        [InlineData(ModbusFunctionCode.GetCommEventCounter, "0B", 0, null, 5)]
        public void GetExpectedPduLength_MatchesTable(
            ModbusFunctionCode functionCode,
            string prefix,
            int quantity,
            string data,
            int expected)
        {
            var request = new ModbusRequest
            {
                SlaveId = 1,
                FunctionCode = functionCode,
                StartAddress = 0,
                Quantity = (ushort)quantity,
                Data = data == null ? null : ParserCorpus.Hex(data)
            };

            Assert.Equal(expected, Validator.GetExpectedPduLength(ParserCorpus.Hex(prefix), request));
        }

        [Fact]
        public void GetExpectedPduLength_InsufficientPrefix_ReturnsMinusOne()
        {
            var request = new ModbusRequest { SlaveId = 1, FunctionCode = ModbusFunctionCode.ReadHoldingRegisters, Quantity = 2 };

            Assert.Equal(-1, Validator.GetExpectedPduLength(ParserCorpus.Hex("03"), request));   // 只有 1 字节
            Assert.Equal(-1, Validator.GetExpectedPduLength(ReadOnlySpan<byte>.Empty, request)); // 空
        }

        [Fact]
        public void GetExpectedPduLength_ExceptionPdu_ReturnsTwo()
        {
            var request = new ModbusRequest { SlaveId = 1, FunctionCode = ModbusFunctionCode.ReadHoldingRegisters, Quantity = 2 };

            Assert.Equal(2, Validator.GetExpectedPduLength(ParserCorpus.Hex("83"), request));
        }

        // ————————————————— Validate —————————————————

        [Fact]
        public void Validate_FunctionCodeMismatch_ReturnsProtocolViolation()
        {
            var request = new ModbusRequest { SlaveId = 1, FunctionCode = ModbusFunctionCode.ReadHoldingRegisters, Quantity = 2 };
            var result = Validator.Validate(ParserCorpus.Hex("04 02 00 01"), request);

            Assert.False(result.IsSuccess);
            Assert.Equal(ModbusErrorKind.ProtocolViolation, result.ErrorKind);
        }

        [Fact]
        public void Validate_ExceptionPdu_ReturnsModbusExceptionWithCode()
        {
            var request = new ModbusRequest { SlaveId = 1, FunctionCode = ModbusFunctionCode.ReadHoldingRegisters, Quantity = 2 };
            var result = Validator.Validate(ParserCorpus.Hex("83 02"), request);

            Assert.False(result.IsSuccess);
            Assert.Equal(ModbusErrorKind.ModbusException, result.ErrorKind);
            Assert.Contains("0x02", result.ErrorMessage);
        }

        [Fact]
        public void Validate_ExceptionWithWrongFunction_ReturnsProtocolViolation()
        {
            var request = new ModbusRequest { SlaveId = 1, FunctionCode = ModbusFunctionCode.ReadHoldingRegisters, Quantity = 2 };
            var result = Validator.Validate(ParserCorpus.Hex("84 02"), request);

            Assert.False(result.IsSuccess);
            Assert.Equal(ModbusErrorKind.ProtocolViolation, result.ErrorKind);
        }

        [Fact]
        public void Validate_ByteCountMismatch_ReturnsProtocolViolation()
        {
            var request = new ModbusRequest { SlaveId = 1, FunctionCode = ModbusFunctionCode.ReadHoldingRegisters, Quantity = 2 };
            var result = Validator.Validate(ParserCorpus.Hex("03 02 00 01"), request);   // 声明 2 字节但只有 1 字节

            Assert.False(result.IsSuccess);
            Assert.Equal(ModbusErrorKind.ProtocolViolation, result.ErrorKind);
        }

        [Fact]
        public void Validate_EchoMismatch_ReturnsProtocolViolation()
        {
            // 0x05 起始地址错误
            var writeCoil = new ModbusRequest
            {
                SlaveId = 1,
                FunctionCode = ModbusFunctionCode.WriteCoil,
                StartAddress = 5,
                Quantity = 1,
                Data = ParserCorpus.Hex("FF 00")
            };
            Assert.Equal(ModbusErrorKind.ProtocolViolation, Validator.Validate(ParserCorpus.Hex("05 00 06 FF 00"), writeCoil).ErrorKind);

            // 0x05 回显值错误
            Assert.Equal(ModbusErrorKind.ProtocolViolation, Validator.Validate(ParserCorpus.Hex("05 00 05 00 00"), writeCoil).ErrorKind);

            // 0x06 起始地址错误
            var writeRegister = new ModbusRequest
            {
                SlaveId = 1,
                FunctionCode = ModbusFunctionCode.WriteHoldingRegister,
                StartAddress = 5,
                Quantity = 1,
                Data = ParserCorpus.Hex("12 34")
            };
            Assert.Equal(ModbusErrorKind.ProtocolViolation, Validator.Validate(ParserCorpus.Hex("06 00 06 12 34"), writeRegister).ErrorKind);

            // 0x06 回显值错误
            Assert.Equal(ModbusErrorKind.ProtocolViolation, Validator.Validate(ParserCorpus.Hex("06 00 05 00 00"), writeRegister).ErrorKind);

            // 0x0F 起始地址错误
            var writeCoils = new ModbusRequest
            {
                SlaveId = 1,
                FunctionCode = ModbusFunctionCode.WriteMultipleCoils,
                StartAddress = 3,
                Quantity = 10,
                Data = ParserCorpus.Hex("01 02")
            };
            Assert.Equal(ModbusErrorKind.ProtocolViolation, Validator.Validate(ParserCorpus.Hex("0F 00 04 00 0A"), writeCoils).ErrorKind);

            // 0x0F 数量错误
            Assert.Equal(ModbusErrorKind.ProtocolViolation, Validator.Validate(ParserCorpus.Hex("0F 00 03 00 0B"), writeCoils).ErrorKind);

            // 0x10 起始地址错误
            var writeRegisters = new ModbusRequest
            {
                SlaveId = 1,
                FunctionCode = ModbusFunctionCode.WriteMultipleHoldingRegisters,
                StartAddress = 3,
                Quantity = 2,
                Data = ParserCorpus.Hex("00 01 02 03")
            };
            Assert.Equal(ModbusErrorKind.ProtocolViolation, Validator.Validate(ParserCorpus.Hex("10 00 04 00 02"), writeRegisters).ErrorKind);

            // 0x10 数量错误
            Assert.Equal(ModbusErrorKind.ProtocolViolation, Validator.Validate(ParserCorpus.Hex("10 00 03 00 03"), writeRegisters).ErrorKind);

            // 0x16 起始地址错误
            var maskWrite = new ModbusRequest
            {
                SlaveId = 1,
                FunctionCode = ModbusFunctionCode.MaskWriteRegister,
                StartAddress = 4,
                Quantity = 1,
                Data = ParserCorpus.Hex("00 F2 00 25")
            };
            Assert.Equal(ModbusErrorKind.ProtocolViolation, Validator.Validate(ParserCorpus.Hex("16 00 05 00 F2 00 25"), maskWrite).ErrorKind);

            // 0x16 AND 掩码错误
            Assert.Equal(ModbusErrorKind.ProtocolViolation, Validator.Validate(ParserCorpus.Hex("16 00 04 00 F3 00 25"), maskWrite).ErrorKind);

            // 0x16 OR 掩码错误
            Assert.Equal(ModbusErrorKind.ProtocolViolation, Validator.Validate(ParserCorpus.Hex("16 00 04 00 F2 00 26"), maskWrite).ErrorKind);
        }

        [Fact]
        public void Validate_ValidPdu_ReturnsExactSlice()
        {
            // 尾部附加多余字节时，只返回期望长度的前缀
            foreach (ParserCorpus.Entry entry in ParserCorpus.All)
            {
                if (entry.Name.StartsWith("exception"))
                    continue;   // 异常响应按契约返回 ModbusException，不属于"合法 PDU"

                byte[] padded = new byte[entry.ValidPdu.Length + 3];
                Buffer.BlockCopy(entry.ValidPdu, 0, padded, 0, entry.ValidPdu.Length);
                padded[^1] = 0xEE;
                padded[^2] = 0xDD;
                padded[^3] = 0xCC;

                var result = Validator.Validate(padded, entry.Request);

                Assert.True(result.IsSuccess, $"{entry.Name}: {result.ErrorMessage}");
                Assert.Equal(entry.ValidPdu.Length, result.Data.Length);
                Assert.True(entry.ValidPdu.AsSpan().SequenceEqual(result.Data.Span), entry.Name);
            }
        }
    }
}