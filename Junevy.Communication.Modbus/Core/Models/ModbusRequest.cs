namespace Junevy.Communication.Modbus.Core.Models
{
    /// <summary>
    /// Modbus 发送数据类，用于封装 Modbus 发送数据（纯数据 DTO，不含事件或行为）。
    /// </summary>
    public class ModbusRequest
    {
        /// <summary>
        /// TCP 事务标识。由 ModbusTCP 客户端在每次请求时自动分配（从 0 递增、回绕），
        /// 用于响应匹配；手动赋值仅对裸帧构建 API（ModbusHelper.BuildRequestFrame）生效。
        /// </summary>
        public ushort TransactionId { get; set; } = 0x0000;

        /// <summary>
        /// 协议类型。传输层以客户端自身协议为准，本属性仅由裸帧构建 API 读取，库不会修改它。
        /// </summary>
        public ModbusProtocolType ProtocolType { get; set; } = ModbusProtocolType.TCP;

        /// <summary>
        /// 从站ID。
        /// </summary>
        public byte SlaveId { get; set; } = 1;

        /// <summary>
        /// 功能码。
        /// </summary>
        public ModbusFunctionCode FunctionCode { get; set; } = ModbusFunctionCode.WriteMultipleHoldingRegisters;

        /// <summary>
        /// 起始地址
        /// </summary>
        public ushort Start { get; set; } = 0x00;

        /// <summary>
        /// 数据长度
        /// </summary>
        public ushort Length { get; set; } = 0x03;

        /// <summary>
        /// 请求数据，含义随 <see cref="FunctionCode"/> 变化：
        /// 0x05 WriteCoil / 0x06 WriteHoldingRegister：恰好 2 字节（大端值）；
        /// 0x0F WriteMultipleCoils / 0x10 WriteMultipleHoldingRegisters：按位/按寄存器打包的数据；
        /// 0x16 MaskWriteRegister：[AndMask(2), OrMask(2)]；
        /// 0x08 Diagnostics：[SubFunction(2), Data...]（原始 PDU 余部）；
        /// 0x17 ReadWriteMultipleRegisters：[ReadStart(2), ReadQty(2), WriteStart(2), WriteQty(2), ByteCount(1), WriteData...]（原始 PDU 余部）；
        /// 其余功能码为 null。
        /// </summary>
        public byte[]? Data { get; set; }
    }
}
