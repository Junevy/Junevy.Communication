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

    /// <summary>
    /// 心跳探测；非 null 时替代内置的心跳包探测。与 <see cref="HealthProbeFactory"/> 互斥：同时设置时构造即抛出 <see cref="ArgumentException"/>。
    /// </summary>
    public IHealthProbe? HealthProbe { get; set; }

    /// <summary>
    /// 心跳探测工厂，适合需要引用通道本身的探测（例如经 <c>RequestAsync</c> 发送协议报文的探测）。与 <see cref="HealthProbe"/> 互斥。
    /// 客户端通道（TCP、UDP、串口）：仅在启用心跳时，于第一次成功打开、启动心跳之前以通道自身（公开实例）调用一次；
    /// 结果在通道生命周期内复用，之后的重连不再调用。工厂抛出异常或返回 null 使本次打开失败（<c>Unspecified</c>），由重连策略处理。
    /// TCP 服务端：启用心跳时，每个会话在启动心跳之前调用一次，参数即该会话（可转换为 <c>ITcpSession</c>）；抛出异常或返回 null 时该会话以 <c>Error</c> 关闭。
    /// </summary>
    public Func<IByteChannel, IHealthProbe>? HealthProbeFactory { get; set; }

    /// <summary>
    /// 重连退避策略；非 null 时覆盖由 <see cref="ReconnectOptions"/> 推导的策略（仍受 Reconnect.Enabled 控制）。
    /// </summary>
    public IBackoffPolicy? ReconnectPolicy { get; set; }
}
