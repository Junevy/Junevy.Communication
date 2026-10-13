using Junevy.Communication.Channels;
using Junevy.Communication.Secs.Protocol;
using Junevy.Communication.Tcp;

namespace Junevy.Communication.Secs.Tests;

/// <summary>
/// 两种模式共用的通道组件（P51-D7、D9）：HSMS 分帧、以 System Bytes 为键的关联、Linktest 探测工厂。
/// 握手钩子由调用方给出——被动端等 Select.req，主动端发 Select.req。
/// </summary>
internal static class HsmsChannelParts
{
    /// <summary>心跳配置：LinktestInterval &gt; 0 才启用；间隔用它、超时用 T6、MaxFailures = 1、只在空闲时探测。</summary>
    /// <param name="timers">计时器快照。</param>
    /// <returns>心跳配置。</returns>
    internal static HeartbeatOptions Heartbeat(HsmsEquipmentTimers timers) => new HeartbeatOptions
    {
        Enabled = timers.LinktestInterval > 0,
        Interval = timers.LinktestInterval > 0 ? timers.LinktestInterval : DefaultHeartbeatInterval,
        Timeout = timers.T6,
        MaxFailures = 1,
        OnlyWhenIdle = true,
    };

    /// <summary>
    /// 通道组件。分帧与关联由 HSMS 自己提供，因此传输层配置里的 Framing / Correlation 保持默认值
    /// （<see cref="ComponentPrecedence"/> 的优先级规则）。
    /// </summary>
    /// <param name="timers">计时器快照。</param>
    /// <param name="initializer">握手钩子。</param>
    /// <returns>通道组件。</returns>
    internal static TcpChannelComponents Components(HsmsEquipmentTimers timers, IConnectionInitializer initializer)
        => new TcpChannelComponents
        {
            FrameCodec = HsmsFraming.Create(timers.MaxMessageLength),
            KeyExtractor = new HsmsKeyExtractor(),
            Initializer = initializer,
            HealthProbeFactory = channel => new HsmsLinktestProbe(channel, timers),
        };

    /// <summary>不发 Linktest 时心跳用的占位间隔（不会被用到，只是给出一个合法值）。</summary>
    private const int DefaultHeartbeatInterval = 1000;
}
