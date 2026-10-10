namespace Junevy.Communication.Channels.Pipeline;

/// <summary>
/// <see cref="StreamChannel"/> 的运行参数（计划 8.1）。由各传输的配置映射而来；<see cref="StreamChannel"/> 构造时校验，非法时抛出 <see cref="ArgumentException"/>（D5）。
/// 超时值 ≤ 0 表示不限时。
/// </summary>
internal sealed class StreamChannelSettings
{
    /// <summary>整帧写出的超时（毫秒）；≤ 0 表示不限时。</summary>
    public int SendTimeout { get; set; }

    /// <summary>请求应答的默认超时（毫秒）；≤ 0 表示不限时。<see cref="RequestOptions.Timeout"/> 为 null 时使用。</summary>
    public int RequestTimeout { get; set; }

    /// <summary>
    /// 迟到应答窗口（毫秒）：-1 表示等于本次请求的超时，0 表示无窗口，其余为固定毫秒数。
    /// Sequential 超时且不重建连接时，窗口期内继续持有请求锁（计划 8.2 第 4 条）。
    /// </summary>
    public int LateReplyWindow { get; set; } = -1;

    /// <summary>未成帧数据超过 <see cref="PartialFrameTimeout"/> 时的处理方式（分帧器为可刷新分帧器时不适用）。</summary>
    public PartialFrameAction PartialFrameAction { get; set; } = PartialFrameAction.Disconnect;

    /// <summary>半帧超时（毫秒）；≤ 0 表示不启用（可刷新分帧器仍按 <see cref="IFlushableFrameDecoder.FlushTimeout"/> 计时）。</summary>
    public int PartialFrameTimeout { get; set; }

    /// <summary>请求超时后是否重建连接。true 时调用 <c>onFault(RequestTimeout)</c>；false 时进入迟到应答窗口（计划 8.2 第 4 条）。</summary>
    public bool ResetOnRequestTimeout { get; set; }

    /// <summary>关联模式。</summary>
    public CorrelationMode Correlation { get; set; } = CorrelationMode.Sequential;

    /// <summary>填充循环每次向内部管道申请的内存大小（字节）。必须为正。</summary>
    public int ReceiveBufferSize { get; set; } = 4096;
}
