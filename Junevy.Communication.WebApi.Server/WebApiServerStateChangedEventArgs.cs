namespace Junevy.Communication.WebApi.Server;

/// <summary>
/// 服务器状态变化的事件参数。事件在状态变化的同一执行流上按变化顺序触发。
/// </summary>
public sealed class WebApiServerStateChangedEventArgs : EventArgs
{
    /// <summary>创建状态变化的事件参数。</summary>
    /// <param name="serverName">服务器名称。</param>
    /// <param name="previous">变化前的状态。</param>
    /// <param name="current">变化后的状态。</param>
    /// <param name="reason">变化的原因（英文，仅供日志与诊断）；没有说明时为 null。</param>
    /// <param name="exception">与变化相关的异常，仅供诊断；没有时为 null。</param>
    public WebApiServerStateChangedEventArgs(string serverName, WebApiServerState previous, WebApiServerState current, string? reason, Exception? exception)
    {
        ServerName = serverName;
        Previous = previous;
        Current = current;
        Reason = reason;
        Exception = exception;
    }

    /// <summary>服务器名称。</summary>
    public string ServerName { get; }

    /// <summary>变化前的状态。</summary>
    public WebApiServerState Previous { get; }

    /// <summary>变化后的状态。</summary>
    public WebApiServerState Current { get; }

    /// <summary>变化的原因（英文）；没有说明时为 null。</summary>
    public string? Reason { get; }

    /// <summary>与变化相关的异常，只作诊断，不参与决策。</summary>
    public Exception? Exception { get; }
}
