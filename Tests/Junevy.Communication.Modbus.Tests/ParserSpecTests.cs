using Junevy.Communication.Modbus.Core.Models;
using Junevy.Communication.Modbus.Core.Parsing;

namespace Junevy.Communication.Modbus.Tests
{
    /// <summary>
    /// 解析器的长期规格测试：基于 <see cref="ParserCorpus"/> 固定 TCP/RTU 的解帧行为、
    /// 噪声容忍与"永不抛异常"契约。这是 P3 Task 3 删掉差分测试与 Legacy 副本后的回归保障。
    /// </summary>
    public class ParserSpecTests
    {
        private static IEnumerable<ParserCorpus.Entry> ValidEntries
            => ParserCorpus.All.Where(e => !e.Name.StartsWith("exception"));

        private static IEnumerable<ParserCorpus.Entry> ExceptionEntry
        {
            get { yield return ParserCorpus.All[^1]; }
        }

        [Fact]
        public void Tcp_ValidFrames_ReturnFullFrameSlice()
        {
            foreach (ParserCorpus.Entry entry in ValidEntries)
            {
                byte[] frame = ParserCorpus.WrapTcp(entry.ValidPdu);
                var result = new TcpProtocolParser().ParseResponse(frame, WithTransactionId(entry.Request));

                Assert.True(result.IsSuccess, $"{entry.Name}: {result.ErrorMessage}");
                // 去掉尾部多余字节后应等于 6 字节 MBAP 头 + 1 字节从站号 + PDU
                byte[] expected = frame.AsSpan(0, 7 + entry.ValidPdu.Length).ToArray();
                Assert.True(expected.AsSpan().SequenceEqual(result.Data.Span), entry.Name);
            }
        }

        [Fact]
        public void Rtu_ValidFrames_ReturnFullFrameSlice()
        {
            foreach (ParserCorpus.Entry entry in ValidEntries)
            {
                byte[] frame = ParserCorpus.WrapRtu(entry.ValidPdu);
                var result = new RtuProtocolParser().ParseResponse(frame, entry.Request);

                Assert.True(result.IsSuccess, $"{entry.Name}: {result.ErrorMessage}");
                Assert.True(frame.AsSpan().SequenceEqual(result.Data.Span), $"{entry.Name}: RTU 帧应包含 CRC");
            }
        }

        [Fact]
        public void Rtu_LeadingGarbage_IsSkipped()
        {
            var parser = new RtuProtocolParser();

            foreach (ParserCorpus.Entry entry in ValidEntries)
            {
                byte[] frame = ParserCorpus.WrapRtu(entry.ValidPdu);
                byte[] withGarbage = new byte[3 + frame.Length];
                withGarbage[0] = 0xAA;
                withGarbage[1] = 0xAA;
                withGarbage[2] = 0xAA;
                Buffer.BlockCopy(frame, 0, withGarbage, 3, frame.Length);

                var result = parser.ParseResponse(withGarbage, entry.Request);

                Assert.True(result.IsSuccess, $"{entry.Name}: {result.ErrorMessage}");
                Assert.True(frame.AsSpan().SequenceEqual(result.Data.Span), entry.Name);
            }
        }

        [Fact]
        public void Rtu_BadCrc_ReturnsFailure()
        {
            var parser = new RtuProtocolParser();
            ParserCorpus.Entry entry = ValidEntries.First();

            byte[] frame = ParserCorpus.WrapRtu(entry.ValidPdu);
            frame[^1] ^= 0xFF;      // 破坏 CRC

            var result = parser.ParseResponse(frame, entry.Request);   // 不得抛异常
            Assert.False(result.IsSuccess);
        }

        [Fact]
        public void Rtu_PartialFrame_ReturnsProtocolViolationWithoutSkipping()
        {
            var parser = new RtuProtocolParser();
            ParserCorpus.Entry entry = ValidEntries.First();

            byte[] frame = ParserCorpus.WrapRtu(entry.ValidPdu);
            byte[] truncated = frame.AsSpan(0, frame.Length - 1).ToArray();

            var result = parser.ParseResponse(truncated, entry.Request);
            Assert.False(result.IsSuccess);
            Assert.Equal(ModbusErrorKind.ProtocolViolation, result.ErrorKind);
            Assert.Contains("too short", result.ErrorMessage, StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public void Tcp_FunctionCodeMismatch_ReturnsProtocolViolation()
        {
            // D1：请求 0x03，响应功能码 0x04
            var request = new ModbusRequest { SlaveId = 1, FunctionCode = ModbusFunctionCode.ReadHoldingRegisters, StartAddress = 0, Quantity = 2, TransactionId = ParserCorpus.TcpTransactionId };
            byte[] frame = ParserCorpus.WrapTcp(ParserCorpus.Hex("04 02 00 01 00 02"), ParserCorpus.TcpTransactionId);

            var result = new TcpProtocolParser().ParseResponse(frame, request);
            Assert.False(result.IsSuccess);
            Assert.Equal(ModbusErrorKind.ProtocolViolation, result.ErrorKind);
        }

        [Fact]
        public void Rtu_FunctionCodeMismatch_ReturnsFailure()
        {
            // D1：请求 0x03，响应功能码 0x04
            var request = new ModbusRequest { SlaveId = 1, FunctionCode = ModbusFunctionCode.ReadHoldingRegisters, StartAddress = 0, Quantity = 2 };
            byte[] frame = ParserCorpus.WrapRtu(ParserCorpus.Hex("04 02 00 01 00 02"));

            var result = new RtuProtocolParser().ParseResponse(frame, request);
            Assert.False(result.IsSuccess);   // D1：被当作噪声跳过，最终扫描失败
        }

        [Fact]
        public void Tcp_Exception_ReturnsModbusExceptionWithCode()
        {
            ParserCorpus.Entry entry = ExceptionEntry.First();
            byte[] frame = ParserCorpus.WrapTcp(entry.ValidPdu, ParserCorpus.TcpTransactionId);

            var result = new TcpProtocolParser().ParseResponse(frame, WithTransactionId(entry.Request));
            Assert.False(result.IsSuccess);
            Assert.Equal(ModbusErrorKind.ModbusException, result.ErrorKind);
            Assert.Contains("0x02", result.ErrorMessage);
            Assert.Equal(9, result.Data.Length);
        }

        [Fact]
        public void Rtu_Exception_ReturnsModbusExceptionWithCode()
        {
            ParserCorpus.Entry entry = ExceptionEntry.First();
            byte[] frame = ParserCorpus.WrapRtu(entry.ValidPdu);

            var result = new RtuProtocolParser().ParseResponse(frame, entry.Request);
            Assert.False(result.IsSuccess);
            Assert.Equal(ModbusErrorKind.ModbusException, result.ErrorKind);
            Assert.Contains("0x02", result.ErrorMessage);
            Assert.Equal(5, result.Data.Length);
        }

        [Fact]
        public void Tcp_TransactionIdMismatch_ReturnsProtocolViolation()
        {
            ParserCorpus.Entry entry = ValidEntries.First();
            byte[] frame = ParserCorpus.WrapTcp(entry.ValidPdu, ParserCorpus.TcpTransactionId);
            var request = WithTransactionId(entry.Request);
            request.TransactionId = 0x1234;

            var result = new TcpProtocolParser().ParseResponse(frame, request);
            Assert.Equal(ModbusErrorKind.ProtocolViolation, result.ErrorKind);
            Assert.Contains("Transaction ID", result.ErrorMessage);
        }

        [Fact]
        public void Tcp_SlaveMismatch_ReturnsProtocolViolation()
        {
            ParserCorpus.Entry entry = ValidEntries.First();
            byte[] frame = ParserCorpus.WrapTcp(entry.ValidPdu, ParserCorpus.TcpTransactionId, slaveId: 9);

            var result = new TcpProtocolParser().ParseResponse(frame, WithTransactionId(entry.Request));
            Assert.Equal(ModbusErrorKind.ProtocolViolation, result.ErrorKind);
            Assert.Contains("Slave ID", result.ErrorMessage);
        }

        [Fact]
        public void Tcp_ProtocolIdNonZero_ReturnsProtocolViolation()
        {
            ParserCorpus.Entry entry = ValidEntries.First();
            byte[] frame = ParserCorpus.WrapTcp(entry.ValidPdu, ParserCorpus.TcpTransactionId);
            frame[3] = 0x01;      // 协议 ID 低字节非 0

            var result = new TcpProtocolParser().ParseResponse(frame, WithTransactionId(entry.Request));
            Assert.Equal(ModbusErrorKind.ProtocolViolation, result.ErrorKind);
            Assert.Contains("protocol ID", result.ErrorMessage, StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public void Parsers_NeverThrow_OnAnyTruncationOrByteFlip()
        {
            var tcp = new TcpProtocolParser();
            var rtu = new RtuProtocolParser();

            foreach (ParserCorpus.Entry entry in ParserCorpus.All)
            {
                byte[] tcpFrame = ParserCorpus.WrapTcp(entry.ValidPdu, ParserCorpus.TcpTransactionId);
                byte[] rtuFrame = ParserCorpus.WrapRtu(entry.ValidPdu);
                var tcpRequest = WithTransactionId(entry.Request);

                for (int length = 0; length < tcpFrame.Length; length++)
                {
                    var truncated = tcpFrame.AsSpan(0, length).ToArray();
                    _ = tcp.ParseResponse(truncated, tcpRequest);
                    _ = rtu.ParseResponse(truncated, entry.Request);
                }

                for (int i = 0; i < tcpFrame.Length; i++)
                {
                    var flippedTcp = (byte[])tcpFrame.Clone();
                    flippedTcp[i] ^= 0xFF;
                    _ = tcp.ParseResponse(flippedTcp, tcpRequest);
                }

                for (int i = 0; i < rtuFrame.Length; i++)
                {
                    var flippedRtu = (byte[])rtuFrame.Clone();
                    flippedRtu[i] ^= 0xFF;
                    _ = rtu.ParseResponse(flippedRtu, entry.Request);
                }
            }
        }

        private static ModbusRequest WithTransactionId(ModbusRequest request)
            => new()
            {
                SlaveId = request.SlaveId,
                FunctionCode = request.FunctionCode,
                StartAddress = request.StartAddress,
                Quantity = request.Quantity,
                Data = request.Data,
                TransactionId = ParserCorpus.TcpTransactionId
            };
    }
}