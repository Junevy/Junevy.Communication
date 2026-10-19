namespace Junevy.Communication.DebugAssistant.Simulators.Faults;

/// <summary>
/// 故障注入的设置（计划第 7.14 节、第 8.1 节）：应答延迟、不应答、强制错误码、破坏校验与断开。
/// 不可变：界面每次修改都替换整个对象，模拟器在处理请求时取得一份快照。
/// </summary>
public sealed record FaultPlan
{
    /// <summary>应答延迟（毫秒），0 表示不延迟。</summary>
    public int DelayMilliseconds { get; init; }

    /// <summary>不应答：请求被处理，但不回复。</summary>
    public bool NoReply { get; init; }

    /// <summary>
    /// 强制错误码：Modbus 异常码、MC 结束码、S7 错误类（高 8 位）与错误码（低 8 位）、FINS 结束码；null 表示不强制。
    /// </summary>
    public int? ForcedErrorCode { get; init; }

    /// <summary>破坏校验：RTU 的 CRC、4C 的和校验，应答的校验字节被改写。</summary>
    public bool CorruptChecksum { get; init; }

    /// <summary>收到下一个请求后断开连接（一次性，用过即清除）。</summary>
    public bool DisconnectOnNextRequest { get; init; }

    /// <summary>没有任何故障。</summary>
    public static FaultPlan None { get; } = new();
}
