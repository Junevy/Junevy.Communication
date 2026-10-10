using Junevy.Communication.Modbus.Core.Models;
using Junevy.Communication.Modbus.Utils;

namespace Junevy.Communication.Modbus.Tests
{
    /// <summary>
    /// 解析器测试语料：对 15 个功能码各构造一个请求与一个合法响应 PDU，另加一个异常响应。
    /// 同时提供把 PDU 封装为 TCP 帧（MBAP）与 RTU 帧（从站号 + CRC）的辅助。
    /// TCP 请求统一使用事务 ID 0x0007、从站号 1；RTU 从站号 1。
    /// </summary>
    internal static class ParserCorpus
    {
        public const ushort TcpTransactionId = 0x0007;
        public const byte SlaveId = 1;

        public sealed record Entry(
            string Name,
            ModbusRequest Request,
            byte[] ValidPdu);

        /// <summary>16 个语料项：前 15 个合法响应，最后一个是对 0x03 的异常响应。</summary>
        public static IReadOnlyList<Entry> All { get; } = new[]
        {
            new Entry("0x01 ReadCoils",
                new ModbusRequest { SlaveId = SlaveId, FunctionCode = ModbusFunctionCode.ReadCoils, StartAddress = 0, Quantity = 10 },
                Hex("01 02 AA 01")),
            new Entry("0x02 ReadDiscreteInputs",
                new ModbusRequest { SlaveId = SlaveId, FunctionCode = ModbusFunctionCode.ReadDiscreteInputs, StartAddress = 0, Quantity = 10 },
                Hex("02 02 AA 01")),
            new Entry("0x03 ReadHoldingRegisters",
                new ModbusRequest { SlaveId = SlaveId, FunctionCode = ModbusFunctionCode.ReadHoldingRegisters, StartAddress = 0, Quantity = 2 },
                Hex("03 04 00 01 00 02")),
            new Entry("0x04 ReadInputRegisters",
                new ModbusRequest { SlaveId = SlaveId, FunctionCode = ModbusFunctionCode.ReadInputRegisters, StartAddress = 0, Quantity = 2 },
                Hex("04 04 00 01 00 02")),
            new Entry("0x05 WriteCoil",
                new ModbusRequest { SlaveId = SlaveId, FunctionCode = ModbusFunctionCode.WriteCoil, StartAddress = 5, Quantity = 1, Data = Hex("FF 00") },
                Hex("05 00 05 FF 00")),
            new Entry("0x06 WriteHoldingRegister",
                new ModbusRequest { SlaveId = SlaveId, FunctionCode = ModbusFunctionCode.WriteHoldingRegister, StartAddress = 5, Quantity = 1, Data = Hex("12 34") },
                Hex("06 00 05 12 34")),
            new Entry("0x07 ReadExceptionStatus",
                new ModbusRequest { SlaveId = SlaveId, FunctionCode = ModbusFunctionCode.ReadExceptionStatus },
                Hex("07 6D")),
            new Entry("0x08 Diagnostics",
                new ModbusRequest { SlaveId = SlaveId, FunctionCode = ModbusFunctionCode.Diagnostics, Data = Hex("00 00 12 34") },
                Hex("08 00 00 12 34")),
            new Entry("0x0B GetCommEventCounter",
                new ModbusRequest { SlaveId = SlaveId, FunctionCode = ModbusFunctionCode.GetCommEventCounter },
                Hex("0B 00 00 00 08")),
            new Entry("0x0C GetCommEventLog",
                new ModbusRequest { SlaveId = SlaveId, FunctionCode = ModbusFunctionCode.GetCommEventLog },
                Hex("0C 08 00 00 00 08 00 0A 01 02")),
            new Entry("0x0F WriteMultipleCoils",
                new ModbusRequest { SlaveId = SlaveId, FunctionCode = ModbusFunctionCode.WriteMultipleCoils, StartAddress = 3, Quantity = 10, Data = Hex("01 02") },
                Hex("0F 00 03 00 0A")),
            new Entry("0x10 WriteMultipleHoldingRegisters",
                new ModbusRequest { SlaveId = SlaveId, FunctionCode = ModbusFunctionCode.WriteMultipleHoldingRegisters, StartAddress = 3, Quantity = 2, Data = Hex("00 01 02 03") },
                Hex("10 00 03 00 02")),
            new Entry("0x11 ReportServerId",
                new ModbusRequest { SlaveId = SlaveId, FunctionCode = ModbusFunctionCode.ReportServerId },
                Hex("11 03 01 FF 55")),
            new Entry("0x16 MaskWriteRegister",
                new ModbusRequest { SlaveId = SlaveId, FunctionCode = ModbusFunctionCode.MaskWriteRegister, StartAddress = 4, Quantity = 1, Data = Hex("00 F2 00 25") },
                Hex("16 00 04 00 F2 00 25")),
            new Entry("0x17 ReadWriteMultipleRegisters",
                new ModbusRequest
                {
                    SlaveId = SlaveId,
                    FunctionCode = ModbusFunctionCode.ReadWriteMultipleRegisters,
                    StartAddress = 0,
                    Quantity = 2,
                    // Data 布局：ReadStart(2) ReadQty(2) WriteStart(2) WriteQty(2) WriteByteCount(1) WriteData
                    // 读起始 0 数量 2；写起始 0 数量 1（值 0x0005）→ 写字节数 2
                    Data = Hex("00 00 00 02 00 00 00 01 02 00 05")
                },
                Hex("17 04 00 01 00 02")),
            new Entry("exception for 0x03",
                new ModbusRequest { SlaveId = SlaveId, FunctionCode = ModbusFunctionCode.ReadHoldingRegisters, StartAddress = 0, Quantity = 2 },
                Hex("83 02"))
        };

        public static byte[] Hex(string spaced)
        {
            string[] parts = spaced.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            var bytes = new byte[parts.Length];
            for (int i = 0; i < parts.Length; i++)
                bytes[i] = Convert.ToByte(parts[i], 16);
            return bytes;
        }

        /// <summary>封装为 TCP 帧：TID(2) 0000 长度(2) 从站号 PDU。长度字段 = 1 + PDU.Length。</summary>
        public static byte[] WrapTcp(byte[] pdu, ushort transactionId = TcpTransactionId, byte slaveId = SlaveId)
        {
            var frame = new byte[7 + pdu.Length];
            frame[0] = (byte)(transactionId >> 8);
            frame[1] = (byte)transactionId;
            frame[2] = 0x00;
            frame[3] = 0x00;
            int length = 1 + pdu.Length;
            frame[4] = (byte)(length >> 8);
            frame[5] = (byte)length;
            frame[6] = slaveId;
            Buffer.BlockCopy(pdu, 0, frame, 7, pdu.Length);
            return frame;
        }

        /// <summary>封装为 RTU 帧：从站号 PDU CRC(低字节在前)。</summary>
        public static byte[] WrapRtu(byte[] pdu, byte slaveId = SlaveId)
        {
            var frame = new byte[1 + pdu.Length + 2];
            frame[0] = slaveId;
            Buffer.BlockCopy(pdu, 0, frame, 1, pdu.Length);
            byte[] crc = Crc16Helper.CrcLittleEndian(frame.AsSpan(0, 1 + pdu.Length));
            frame[1 + pdu.Length] = crc[0];
            frame[2 + pdu.Length] = crc[1];
            return frame;
        }
    }
}