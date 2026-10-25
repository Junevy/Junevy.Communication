using Junevy.Communication.Core.Results;

namespace Junevy.Communication.DebugAssistant.Sessions;

/// <summary>
/// 操作记录（计划 U6）：每次调用的时间、操作、目标、耗时、成败、错误种类、协议错误码与库的消息。
/// </summary>
/// <param name="ProtocolCode">协议错误码的显示文本（十六进制），没有时为 null。</param>
public sealed record OperationRecord(
    DateTimeOffset Time,
    string Operation,
    string Target,
    TimeSpan Elapsed,
    bool Succeeded,
    CommErrorKind ErrorKind,
    string? ProtocolCode,
    string Message)
{
    /// <summary>错误种类的中文名称。</summary>
    public string ErrorKindText => CommErrorKindNames.Of(ErrorKind);

    /// <summary>成败的显示文本。</summary>
    public string ResultText => Succeeded ? "成功" : "失败";

    /// <summary>耗时的显示文本（毫秒）。</summary>
    public string ElapsedText => $"{Elapsed.TotalMilliseconds:0} ms";
}
