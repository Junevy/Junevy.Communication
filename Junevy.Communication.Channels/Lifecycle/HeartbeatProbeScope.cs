namespace Junevy.Communication.Channels.Lifecycle;

/// <summary>
/// 心跳探测上下文（内部）。<see cref="HeartbeatMonitor"/> 在调用 <see cref="IHealthProbe.ProbeAsync"/> 期间建立它，探测返回后立即关闭。
/// 上下文挂在 <see cref="AsyncLocal{T}"/> 上，因此探测自身发出的请求（包括协议自定义探测经 RequestAsync 发出的请求）能被识别，
/// 而其他异步流（用户请求）不受影响。<c>StreamChannel</c> 据此判定请求超时是否重建连接（ResetOnRequestTimeout）。
/// </summary>
/// <remarks>
/// 上下文是进程内的静态状态：探测期间对其他通道发出的请求超时，同样不会重建那些通道的连接。
/// 探测返回并关闭之后，探测派生但未等待的请求不再享有豁免。
/// 监视器因超时放弃探测时，探测仍在运行的部分直到探测自身返回为止都享有豁免。
/// </remarks>
internal sealed class HeartbeatProbeScope : IDisposable
{
    private static readonly AsyncLocal<HeartbeatProbeScope?> Ambient = new AsyncLocal<HeartbeatProbeScope?>();

    private readonly HeartbeatProbeScope? outer;
    private int open = 1;

    private HeartbeatProbeScope(HeartbeatProbeScope? outer)
    {
        this.outer = outer;
    }

    /// <summary>
    /// 当前执行流是否位于尚未返回的心跳探测之内。
    /// </summary>
    internal static bool IsActive
    {
        get
        {
            HeartbeatProbeScope? scope = Ambient.Value;
            return scope != null && Volatile.Read(ref scope.open) != 0;
        }
    }

    /// <summary>
    /// 开启一次探测的上下文，并挂到当前执行流上。调用方以 using 包裹探测的启动代码，Dispose 时恢复此前的上下文；
    /// 探测自身的续延在启动时已经捕获本上下文，不受 Dispose 影响。
    /// </summary>
    /// <returns>探测上下文；探测返回后必须调用 <see cref="Close"/>。</returns>
    internal static HeartbeatProbeScope Enter()
    {
        var scope = new HeartbeatProbeScope(Ambient.Value);
        Ambient.Value = scope;
        return scope;
    }

    /// <summary>
    /// 关闭上下文（探测返回后调用，可重复调用）：此后该上下文内发出的请求按普通请求处理。
    /// </summary>
    internal void Close() => Volatile.Write(ref open, 0);

    /// <summary>恢复 <see cref="Enter"/> 之前的上下文。</summary>
    public void Dispose() => Ambient.Value = outer;
}
