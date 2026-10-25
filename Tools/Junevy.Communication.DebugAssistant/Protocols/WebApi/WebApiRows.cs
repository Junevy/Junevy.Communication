namespace Junevy.Communication.DebugAssistant.Protocols.WebApi;

/// <summary>查询参数或请求头的一行（名称与值都是文字）。</summary>
public sealed record WebApiNameValueRow(string Name, string Value);

/// <summary>
/// 调用时间线的一行（计划 7.9 "事件时间线"）：发送、响应、完成与可用性变化，逐次尝试一行。
/// </summary>
public sealed record WebApiTimelineRow(string Time, string Event, string Attempt, string Status, string Detail);

/// <summary>服务端请求日志的一行（计划 7.11 "请求日志"）。</summary>
public sealed record ServerRequestRow(
    string Time,
    string Method,
    string Path,
    string Route,
    string Outcome,
    string Status,
    string Elapsed);
