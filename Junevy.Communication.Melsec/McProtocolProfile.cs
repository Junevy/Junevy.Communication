using Junevy.Communication.Channels;
using Junevy.Communication.Melsec.Framing;

namespace Junevy.Communication.Melsec;

/// <summary>
/// MELSEC 的协议配置（P2 计划 Task 11、P25 Task 8）：按通道的交付方式与介质，把分帧、关联、键提取器与心跳探测工厂填入通道组件，返回同一实例。
/// 组件中这些属性已由调用方设置时抛出 <see cref="ArgumentException"/>，不静默覆盖；帧类型与介质的规则只在这里校验。
/// </summary>
public static class McProtocolProfile
{
    /// <summary>
    /// 为 MELSEC 配置通道组件：字节流设置分帧，数据报不设分帧；关联、键提取器与心跳探测两者都设置。
    /// </summary>
    /// <typeparam name="TComponents">通道组件类型。</typeparam>
    /// <param name="components">要配置的组件。</param>
    /// <param name="options">MELSEC 选项（取快照）。</param>
    /// <param name="delivery">通道的交付方式，即创建器的 <see cref="IChannelCreator.Delivery"/>。</param>
    /// <param name="medium">通道的介质，即创建器的 <see cref="IChannelCreator.Medium"/>。</param>
    /// <returns>同一组件实例。</returns>
    /// <exception cref="ArgumentNullException"><paramref name="components"/> 为 null。</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="delivery"/>、<paramref name="medium"/> 或选项中的帧类型未定义。</exception>
    /// <exception cref="ArgumentException">
    /// 组件中的分帧、关联、键提取器或心跳探测已由调用方设置；或帧类型与介质不匹配（以太网只接受 3E、4E，串口只接受 4C）；或数据报使用 4C。
    /// </exception>
    public static TComponents Configure<TComponents>(TComponents components, McClientOptions options, ChannelDelivery delivery, ChannelMedium medium)
        where TComponents : ChannelComponents
        => Configure(components, McProtocolSettings.From(options), delivery, medium);

    internal static TComponents Configure<TComponents>(TComponents components, McProtocolSettings settings, ChannelDelivery delivery, ChannelMedium medium)
        where TComponents : ChannelComponents
    {
        RequireComponents(components);
        RequireUnset(components);
        RequireFrame(settings, delivery, medium);
        if (delivery == ChannelDelivery.Stream)
            components.FrameCodec = McStreamFraming.Create(settings, FramingOptions.DefaultMaxFrameLength);

        ApplyCorrelation(components, settings);
        return components;
    }

    private static void RequireComponents(ChannelComponents? components)
    {
        if (components == null)
            throw new ArgumentNullException(nameof(components));
    }

    private static void RequireUnset(ChannelComponents components)
    {
        if (components.FrameCodec != null)
            ThrowAlreadySet(nameof(ChannelComponents.FrameCodec));
        if (components.Correlation.HasValue)
            ThrowAlreadySet(nameof(ChannelComponents.Correlation));
        if (components.KeyExtractor != null)
            ThrowAlreadySet(nameof(ChannelComponents.KeyExtractor));
        if (components.HealthProbe != null)
            ThrowAlreadySet(nameof(ChannelComponents.HealthProbe));
        if (components.HealthProbeFactory != null)
            ThrowAlreadySet(nameof(ChannelComponents.HealthProbeFactory));
    }

    private static void ThrowAlreadySet(string property)
        => throw new ArgumentException(
            $"ChannelComponents.{property} is already set; McProtocolProfile sets it for MELSEC and does not override it.", "components");

    // 帧类型与介质、交付方式的规则只在这里：以太网（TCP、UDP）只接受 3E、4E，串口只接受 4C，数据报不接受 4C。
    private static void RequireFrame(McProtocolSettings settings, ChannelDelivery delivery, ChannelMedium medium)
    {
        if (!Enum.IsDefined(typeof(ChannelDelivery), delivery))
            throw new ArgumentOutOfRangeException(nameof(delivery), delivery, "The channel delivery is not defined.");
        if (!Enum.IsDefined(typeof(ChannelMedium), medium))
            throw new ArgumentOutOfRangeException(nameof(medium), medium, "The channel medium is not defined.");
        if (delivery == ChannelDelivery.Datagram && settings.Is4C)
            throw new ArgumentException("Datagram transports carry 3E and 4E frames only; 4C frames are serial.", "options");
        if (medium == ChannelMedium.Network && settings.Is4C)
            throw new ArgumentException("Network transports (TCP, UDP) carry 3E and 4E frames only; 4C frames are serial.", "options");
        if (medium == ChannelMedium.Serial && !settings.Is4C)
            throw new ArgumentException("Serial transports carry 4C frames only; set FrameType to Frame4C.", "options");
    }

    // 3E / 4E 的应答按序列号关联（4E 为 Keyed，3E 与 4C 为 Sequential）；心跳探测由通道在启用心跳时调用一次。
    private static void ApplyCorrelation(ChannelComponents components, McProtocolSettings settings)
    {
        components.Correlation = settings.Is4E ? CorrelationMode.Keyed : CorrelationMode.Sequential;
        components.KeyExtractor = settings.Is4E ? new McSerialKeyExtractor(settings.Encoding) : null;
        components.HealthProbeFactory = channel => new McLoopbackProbe(channel, settings);
    }
}
