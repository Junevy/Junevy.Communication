namespace Junevy.Communication.DebugAssistant.Common;

/// <summary>发送前追加到负载末尾的校验（计划 DA-D12）。库不计算校验和，由协议或助手负责。</summary>
public enum ChecksumKind
{
    None,
    Sum8,
    Xor8,
    Crc16Modbus,
    Crc16CcittFalse,
    Lrc,
}
