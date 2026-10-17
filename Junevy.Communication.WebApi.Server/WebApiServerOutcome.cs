namespace Junevy.Communication.WebApi.Server;

/// <summary>
/// 一次请求的结果分类（P3.2 计划第 16 节的对照表）。通过 RequestCompleted 事件报告，每个请求恰好报告一次。
/// </summary>
public enum WebApiServerOutcome
{
    /// <summary>处理器成功。</summary>
    Succeeded,

    /// <summary>处理器返回了业务失败。</summary>
    BusinessFailure,

    /// <summary>框架或拦截器拒绝（400、401、404、405、408、409、413、415、422、503 等）。</summary>
    Rejected,

    /// <summary>处理器或拦截器抛出异常（500）。</summary>
    HandlerFailed,

    /// <summary>超时（504，或读取正文超时的 408 之外的超时）。</summary>
    TimedOut,

    /// <summary>去重重放：返回第一次的响应，处理器没有执行。</summary>
    Replayed,

    /// <summary>对端已断开，响应没能写出。</summary>
    ClientDisconnected,
}
