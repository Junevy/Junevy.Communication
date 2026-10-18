namespace Junevy.Communication.WebApi.Server;

/// <summary>
/// HTTP 服务端的生命周期状态（P32-D4）。状态只在生命周期锁内变化，不做故障自动重启。
/// </summary>
public enum WebApiServerState
{
    /// <summary>未启动：初始状态，也是启动失败与停止之后的状态。</summary>
    Stopped,

    /// <summary>正在绑定地址并启动应用。</summary>
    Starting,

    /// <summary>正在接收请求。</summary>
    Running,

    /// <summary>正在停止：不再接收新请求，等待在途请求结束或超过 <see cref="WebApiServerConfig.StopTimeout"/>。</summary>
    Stopping,
}
