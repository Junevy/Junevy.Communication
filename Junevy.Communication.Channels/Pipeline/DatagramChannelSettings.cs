namespace Junevy.Communication.Channels.Pipeline;

/// <summary>
/// <see cref="DatagramChannel"/> 的运行参数（计划 15.2）。由 UDP 配置映射而来；<see cref="DatagramChannel"/> 构造时复制并校验，非法时抛出 <see cref="ArgumentException"/>（D5）。
/// 超时值 ≤ 0 表示不限时。
/// </summary>
internal sealed class DatagramChannelSettings
{
    /// <summary>单个数据报写出的超时（毫秒）；≤ 0 表示不限时。</summary>
    public int SendTimeout { get; set; }

    /// <summary>请求应答的默认超时（毫秒）；≤ 0 表示不限时。每次尝试单独计时。<see cref="RequestOptions.Timeout"/> 为 null 时使用。</summary>
    public int RequestTimeout { get; set; }

    /// <summary>请求超时后原样重发的次数（≥ 0）。全部尝试超时后才进入迟到应答窗口。</summary>
    public int RequestRetryCount { get; set; }

    /// <summary>
    /// 迟到应答窗口（毫秒）：-1 表示等于本次请求的超时，0 表示无窗口，其余为固定毫秒数。
    /// Sequential 请求在窗口期内继续持有请求锁，与 StreamChannel 的非重建路径一致。
    /// </summary>
    public int LateReplyWindow { get; set; } = -1;

    /// <summary>关联模式。</summary>
    public CorrelationMode Correlation { get; set; } = CorrelationMode.Sequential;

    /// <summary>数据报的最大负载（字节），范围 [1, 65507]；超过的数据报丢弃并计为协议错误。</summary>
    public int MaxDatagramSize { get; set; } = 65507;
}
