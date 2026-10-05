namespace Junevy.Communication.Modbus.Core.Models
{
    /// <summary>
    /// 请求失败的机器可读分类，供调用方做重连/重试/告警决策。
    /// </summary>
    public enum ModbusErrorKind
    {
        /// <summary>成功（未失败）。</summary>
        None = 0,

        /// <summary>未分类失败（历史兼容默认值）。</summary>
        Unspecified = 1,

        /// <summary>本地请求校验失败。</summary>
        InvalidRequest = 2,

        /// <summary>连接已断开/发送失败。</summary>
        ConnectionClosed = 3,

        /// <summary>超时（连接/读/写）。</summary>
        Timeout = 4,

        /// <summary>帧格式/CRC/长度违规。</summary>
        ProtocolViolation = 5,

        /// <summary>从站返回 Modbus 异常。</summary>
        ModbusException = 6,

        /// <summary>操作被取消。</summary>
        Cancelled = 7,
    }
}
