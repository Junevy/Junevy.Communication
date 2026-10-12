using Junevy.Communication.Channels;
using Junevy.Communication.Omron.Framing;

namespace Junevy.Communication.Omron;

/// <summary>
/// FINS 的通道接线（计划 Task 13 第 4 条、P4-D8、P4-D10）：按 FINS/TCP 长度分帧、应答按发送顺序关联、
/// 节点地址握手与心跳探测指向本协议的实现。组件里这些属性已由调用方设置时抛出 <see cref="ArgumentException"/>，不静默覆盖。
/// 数据报交付的通道没有 FINS/TCP 分帧可用，因此不接受 FINS（P4-D10）。
/// </summary>
internal static class FinsProtocolProfile
{
    /// <summary>为 FINS 配置通道组件。</summary>
    /// <typeparam name="TComponents">通道组件类型。</typeparam>
    /// <param name="components">要配置的组件。</param>
    /// <param name="settings">连接参数快照。</param>
    /// <param name="session">握手结果写入的会话状态。</param>
    /// <param name="delivery">通道的交付方式。</param>
    /// <returns>同一组件实例。</returns>
    /// <exception cref="ArgumentException">组件中的同名属性已被设置；或通道交付数据报而不是字节流。</exception>
    public static TComponents Configure<TComponents>(TComponents components, FinsProtocolSettings settings, FinsSession session, ChannelDelivery delivery)
        where TComponents : ChannelComponents
    {
        RequireUnset(components);
        if (delivery != ChannelDelivery.Stream)
            throw new ArgumentException("FINS runs over TCP and needs a stream so the shell length can frame it; datagram transports carry no FINS stream.", nameof(delivery));

        components.FrameCodec = FinsFraming.Create();

        // P4-D8：FINS 的 SID 只有 8 位且会回绕，握手帧又没有关联键，因此按发送顺序匹配应答，SID 的一致性由协议层校验。
        components.Correlation = CorrelationMode.Sequential;
        components.Initializer = new FinsNodeHandshake(settings, session);

        // P4-D15：探测要经通道自身的 RequestAsync 发协议报文，因此用工厂形式拿到通道引用。
        components.HealthProbeFactory = channel => new FinsReadProbe(channel, session);
        return components;
    }

    private static void RequireUnset(ChannelComponents components)
    {
        if (components.FrameCodec != null)
            ThrowAlreadySet(nameof(ChannelComponents.FrameCodec));
        if (components.Correlation.HasValue)
            ThrowAlreadySet(nameof(ChannelComponents.Correlation));
        if (components.KeyExtractor != null)
            ThrowAlreadySet(nameof(ChannelComponents.KeyExtractor));
        if (components.Initializer != null)
            ThrowAlreadySet(nameof(ChannelComponents.Initializer));
        if (components.HealthProbe != null)
            ThrowAlreadySet(nameof(ChannelComponents.HealthProbe));
        if (components.HealthProbeFactory != null)
            ThrowAlreadySet(nameof(ChannelComponents.HealthProbeFactory));
    }

    private static void ThrowAlreadySet(string property)
        => throw new ArgumentException(
            $"ChannelComponents.{property} is already set; FinsProtocolProfile sets it for FINS and does not override it.", "components");
}
