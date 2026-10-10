namespace Junevy.Communication.Tcp;

/// <summary>
/// TCP 服务端的状态（设计文档 7.2）。
/// </summary>
public enum ServerState
{
    /// <summary>已停止：初始状态，或停止完成之后。</summary>
    Stopped,

    /// <summary>正在绑定监听地址。</summary>
    Starting,

    /// <summary>正在监听并接受连接。</summary>
    Running,

    /// <summary>正在停止：关闭监听器并关闭全部会话。</summary>
    Stopping,

    /// <summary>监听器故障（接受循环异常）；启用 RestartOnFault 时在后台重新监听，成功后回到 Running。</summary>
    Faulted,
}
