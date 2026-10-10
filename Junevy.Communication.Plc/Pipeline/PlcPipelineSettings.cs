namespace Junevy.Communication.Plc.Pipeline;

/// <summary>
/// 流水线设置：客户端构造时从 <see cref="PlcClientOptions"/> 校验并取得的快照（D5）。
/// </summary>
internal sealed class PlcPipelineSettings
{
    /// <summary>
    /// 校验选项并取快照。
    /// </summary>
    /// <param name="options">协议选项。</param>
    /// <exception cref="ArgumentOutOfRangeException"><see cref="PlcClientOptions.RetryCount"/> 或 <see cref="PlcClientOptions.RetryInterval"/> 为负。</exception>
    public PlcPipelineSettings(PlcClientOptions options)
    {
        if (options.RetryCount < 0)
            throw new ArgumentOutOfRangeException(nameof(PlcClientOptions.RetryCount), options.RetryCount, "The retry count must not be negative.");
        if (options.RetryInterval < 0)
            throw new ArgumentOutOfRangeException(nameof(PlcClientOptions.RetryInterval), options.RetryInterval, "The retry interval must not be negative.");

        RetryCount = options.RetryCount;
        RetryInterval = options.RetryInterval;
        AllowReadModifyWriteBits = options.AllowReadModifyWriteBits;
    }

    /// <summary>可重试失败的最大重试次数。</summary>
    public int RetryCount { get; }

    /// <summary>重试前等待链路恢复的上限（毫秒）。</summary>
    public int RetryInterval { get; }

    /// <summary>是否允许通过读-改-写写入字中的位。</summary>
    public bool AllowReadModifyWriteBits { get; }
}
