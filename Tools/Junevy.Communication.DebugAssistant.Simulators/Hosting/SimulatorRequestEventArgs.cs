namespace Junevy.Communication.DebugAssistant.Simulators.Hosting;

/// <summary>
/// 请求日志的一条：时间、对端、请求帧、引擎的结论、应答帧与摘要（计划第 7.14 节"请求日志"）。
/// </summary>
public sealed record SimulatorRequestEventArgs(
    DateTimeOffset At,
    string? RemoteEndPoint,
    byte[] Request,
    SimulatorReplyKind Kind,
    byte[]? Response,
    string Summary);
