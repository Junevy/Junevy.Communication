namespace Junevy.Communication.Tcp;

/// <summary>
/// TCP 保活（KeepAlive）配置。net8.0 按秒设置空闲时间、探测间隔与重试次数（毫秒向上取整为秒）；
/// net472 只能经 <c>SIO_KEEPALIVE_VALS</c> 设置空闲时间与间隔，重试次数由系统决定（Windows 默认 10 次），<see cref="RetryCount"/> 在 net472 上不生效。
/// </summary>
public sealed class TcpKeepAliveOptions
{
    /// <summary>是否启用 TCP 保活，默认 true。</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>空闲多久后开始探测（毫秒），默认 30000。启用时必须为正。</summary>
    public int Time { get; set; } = 30000;

    /// <summary>探测间隔（毫秒），默认 5000。启用时必须为正。</summary>
    public int Interval { get; set; } = 5000;

    /// <summary>探测无应答时的重试次数，默认 3。启用时必须至少为 1；只在 net8.0 生效。</summary>
    public int RetryCount { get; set; } = 3;
}
