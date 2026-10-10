using Junevy.Communication.Core.Resilience;

namespace Junevy.Communication.Channels;

/// <summary>
/// 代码级覆盖：协议包通过它把自己的分帧、关联、握手、心跳交给通道；优先级高于配置中的同类设置。
/// 协议声明"我这样分帧、这样匹配应答、连上后这样握手、这样探活"，通道照做。
/// </summary>
public class ChannelComponents
{
    /// <summary>分帧编解码工厂；非 null 时覆盖配置中的 Framing。</summary>
    public IFrameCodecFactory? FrameCodec { get; set; }

    /// <summary>关联模式；非 null 时覆盖配置中的 Correlation。</summary>
    public CorrelationMode? Correlation { get; set; }

    /// <summary>关联键提取器，<see cref="CorrelationMode.Keyed"/> 模式使用。</summary>
    public IFrameKeyExtractor? KeyExtractor { get; set; }

    /// <summary>握手钩子：连通后、进入 Connected 之前执行，每次重连都会重新执行。</summary>
    public IConnectionInitializer? Initializer { get; set; }

    /// <summary>心跳探测；非 null 时替代内置的心跳包探测。</summary>
    public IHealthProbe? HealthProbe { get; set; }

    /// <summary>
    /// 重连退避策略；非 null 时覆盖由 <see cref="ReconnectOptions"/> 推导的策略（仍受 Reconnect.Enabled 控制）。
    /// </summary>
    public IBackoffPolicy? ReconnectPolicy { get; set; }
}
