namespace Junevy.Communication.Modbus.Core.Models
{
    /// <summary>
    /// Modbus 发送数据类，用于封装 Modbus 发送数据。
    /// </summary>
    public class ModbusRequest
    {
        /// <summary>
        /// 功能码改变事件
        /// </summary>
        public event Action<ModbusFunctionCode>? OnFunctionCodeChanged;

        /// <summary>
        /// 协议类型改变事件
        /// </summary>
        public event Action<ModbusProtocolType>? OnProtocolTypeChanged;

        /// <summary>
        /// TCP 事务标识。由 ModbusTCP 客户端在每次请求时自动分配（从 0 递增、回绕），
        /// 用于响应匹配；手动赋值仅对裸帧构建 API（ModbusHelper.BuildRequestFrame）生效。
        /// </summary>
        public ushort TransactionId { get; set; } = 0x0000;
        
        private ModbusProtocolType protocolType = ModbusProtocolType.TCP;
        /// <summary>
        /// 协议类型。传输层以客户端自身协议为准，本属性仅由裸帧构建 API 读取，库不会修改它。
        /// </summary>
        public ModbusProtocolType ProtocolType
        {
            get => protocolType;
            set
            {
                protocolType = value;
                OnProtocolTypeChanged?.Invoke(protocolType);
            }
        }

        /// <summary>
        /// 从站ID。
        /// </summary>
        public byte SlaveId { get; set; } = 1;

        /// <summary>
        /// 功能码。
        /// </summary>
        private ModbusFunctionCode functionCode = ModbusFunctionCode.WriteMultipleHoldingRegisters;
        public ModbusFunctionCode FunctionCode 
        {
            get => functionCode;
            set {
                functionCode = value;
                InvokeOnFunctionCodeChanged();  // 调用事件
            }
        }

        /// <summary>
        /// 起始地址
        /// </summary>
        public ushort Start { get; set; } = 0x00;

        /// <summary>
        /// 数据长度
        /// </summary>
        public ushort Length { get; set; } = 0x03;

        /// <summary>
        /// 数据。
        /// </summary>
        public byte[]? Data { get; set; }


        /// <summary>
        /// 功能码改变事件。
        /// </summary>
        public void InvokeOnFunctionCodeChanged()
        {
            OnFunctionCodeChanged?.Invoke(FunctionCode);
        }
    }
}
