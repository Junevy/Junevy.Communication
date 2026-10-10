namespace Junevy.Communication.Channels;

/// <summary>
/// 重连配置（设计文档第 5.5 节）。
/// </summary>
public sealed class ReconnectOptions
{
    /// <summary>是否启用自动重连，默认 false，与 Modbus 的 Reconnect=false 一致。</summary>
    public bool Enabled { get; set; }

    /// <summary>退避方式，默认 <see cref="ReconnectMode.ExponentialBackoff"/>。</summary>
    public ReconnectMode Mode { get; set; } = ReconnectMode.ExponentialBackoff;

    /// <summary>固定间隔或指数退避的初始间隔（毫秒），默认 1000。</summary>
    public int Interval { get; set; } = 1000;

    /// <summary>指数退避的最大间隔（毫秒），默认 30000。</summary>
    public int MaxInterval { get; set; } = 30000;

    /// <summary>最大重连次数；0 表示无限。</summary>
    public int MaxAttempts { get; set; }

    /// <summary>首次连接失败也转入后台重连（设备晚于软件上电、USB 串口未插），默认 false。</summary>
    public bool OnInitialFailure { get; set; }
}
