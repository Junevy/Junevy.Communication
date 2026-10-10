using Junevy.Communication.Channels;
using Junevy.Communication.Melsec.Framing;

namespace Junevy.Communication.Melsec;

/// <summary>
/// MELSEC 的协议配置（P2 计划 Task 11）：把分帧、关联、键提取器与心跳探测工厂填入通道组件，返回同一实例。
/// 组件中这些属性已由调用方设置时抛出 <see cref="ArgumentException"/>，不静默覆盖。
/// </summary>
public static class McProtocolProfile
{
    /// <summary>为流式传输（TCP、串口）配置分帧、关联与心跳探测。</summary>
    /// <typeparam name="TComponents">通道组件类型。</typeparam>
    /// <param name="components">要配置的组件。</param>
    /// <param name="options">MELSEC 选项（取快照）。</param>
    /// <returns>同一组件实例。</returns>
    /// <exception cref="ArgumentException">组件中的分帧、关联、键提取器或心跳探测已由调用方设置。</exception>
    public static TComponents ConfigureStream<TComponents>(TComponents components, McClientOptions options) where TComponents : ChannelComponents
        => ConfigureStream(components, McProtocolSettings.From(options));

    /// <summary>为数据报传输（UDP）配置关联与心跳探测；数据报不设分帧。</summary>
    /// <typeparam name="TComponents">通道组件类型。</typeparam>
    /// <param name="components">要配置的组件。</param>
    /// <param name="options">MELSEC 选项（取快照）。</param>
    /// <returns>同一组件实例。</returns>
    /// <exception cref="ArgumentException">组件已由调用方设置，或选项为 4C 帧（4C 只用于串口）。</exception>
    public static TComponents ConfigureDatagram<TComponents>(TComponents components, McClientOptions options) where TComponents : ChannelComponents
        => ConfigureDatagram(components, McProtocolSettings.From(options), nameof(options));

    internal static TComponents ConfigureStream<TComponents>(TComponents components, McProtocolSettings settings) where TComponents : ChannelComponents
    {
        RequireComponents(components);
        RequireUnset(components);
        components.FrameCodec = McStreamFraming.Create(settings, new FramingOptions().MaxFrameLength);
        ApplyCorrelation(components, settings);
        return components;
    }

    internal static TComponents ConfigureDatagram<TComponents>(TComponents components, McProtocolSettings settings, string paramName)
        where TComponents : ChannelComponents
    {
        RequireComponents(components);
        if (settings.Is4C)
            throw new ArgumentException("Datagram transports carry 3E and 4E frames only; 4C frames are serial.", paramName);

        RequireUnset(components);
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

    // 3E / 4E 的应答按序列号关联（4E 为 Keyed，3E 与 4C 为 Sequential）；心跳探测由通道在启用心跳时调用一次。
    private static void ApplyCorrelation(ChannelComponents components, McProtocolSettings settings)
    {
        components.Correlation = settings.Is4E ? CorrelationMode.Keyed : CorrelationMode.Sequential;
        components.KeyExtractor = settings.Is4E ? new McSerialKeyExtractor(settings.Encoding) : null;
        components.HealthProbeFactory = channel => new McLoopbackProbe(channel, settings);
    }
}
