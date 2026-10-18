namespace Junevy.Communication.WebApi;

/// <summary>
/// 对端（MES）的可用性，由真实请求的结果与可选的健康检查推断。
/// </summary>
public enum WebApiAvailability
{
    /// <summary>尚未收到任何判断依据（初始状态）。</summary>
    Unknown = 0,

    /// <summary>最近一次结果表明对端可达。</summary>
    Online = 1,

    /// <summary>连续失败次数达到离线阈值，对端被视为不可达。</summary>
    Offline = 2,
}
