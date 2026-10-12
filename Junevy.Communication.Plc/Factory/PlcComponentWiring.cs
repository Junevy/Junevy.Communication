using Junevy.Communication.Channels;

namespace Junevy.Communication.Plc;

/// <summary>
/// 协议包接线的公用部分：把"我这样分帧、这样匹配应答、连上后这样握手、这样探活"一次性填进 <see cref="ChannelComponents"/>，
/// 并保证不静默覆盖调用方已经填好的值。所有守卫通过之后才赋值，因此不会留下半配置的组件。
/// </summary>
/// <remarks>
/// <para>协议**总是拥有**五个属性：<see cref="ChannelComponents.FrameCodec"/>、<see cref="ChannelComponents.Correlation"/>、
/// <see cref="ChannelComponents.KeyExtractor"/>、<see cref="ChannelComponents.HealthProbe"/> 与 <see cref="ChannelComponents.HealthProbeFactory"/>。
/// 调用方已经设置其中任何一项时抛 <see cref="ArgumentException"/>（参数名 <c>components</c>），而不是覆盖调用方的决定。</para>
/// <para><see cref="ChannelComponents.Initializer"/> 只在协议**提供了**初始化器（<c>initializer</c> 非 null）时才属于协议：此时预设即视为冲突；
/// 协议不提供初始化器时不检查也不修改，调用方自己的握手钩子原样保留。</para>
/// <para>本方法不碰 <see cref="ChannelComponents.ReconnectPolicy"/>——重连退避是宿主的选择，不是协议的声明。</para>
/// <para>交付方式与介质的规则（例如"某个协议只能跑在字节流上"、"帧类型与介质必须匹配"）由各协议在调用本方法**之前**检查，
/// 因为那些规则随协议而不同。</para>
/// </remarks>
public static class PlcComponentWiring
{
    private const string AlreadySetFormat = "ChannelComponents.{0} is already set; {1} sets it and does not override it.";

    /// <summary>按协议的声明配置通道组件，并返回同一实例。</summary>
    /// <typeparam name="TComponents">通道组件类型。</typeparam>
    /// <param name="components">要配置的组件；就地修改。</param>
    /// <param name="owner">拥有这些组件的协议名，只出现在异常消息里，便于调用方定位冲突来源。</param>
    /// <param name="frameCodec">分帧编解码工厂；为 null 时不设分帧（数据报交付的协议没有分帧可用）。</param>
    /// <param name="correlation">关联模式。</param>
    /// <param name="keyExtractor">关联键提取器；<see cref="CorrelationMode.Keyed"/> 时非 null，其余可为 null。</param>
    /// <param name="initializer">握手钩子；为 null 时协议不提供握手，调用方自己的钩子保留。</param>
    /// <param name="healthProbeFactory">心跳探测工厂；探测需要引用通道本身时用工厂形式拿到通道。</param>
    /// <returns>同一组件实例。</returns>
    /// <exception cref="ArgumentNullException"><paramref name="components"/> 或 <paramref name="healthProbeFactory"/> 为 null。</exception>
    /// <exception cref="ArgumentException"><paramref name="owner"/> 为 null 或空白；或 <paramref name="components"/> 中协议拥有的属性已由调用方设置。</exception>
    public static TComponents Apply<TComponents>(
        TComponents components,
        string owner,
        IFrameCodecFactory? frameCodec,
        CorrelationMode correlation,
        IFrameKeyExtractor? keyExtractor,
        IConnectionInitializer? initializer,
        Func<IByteChannel, IHealthProbe> healthProbeFactory)
        where TComponents : ChannelComponents
    {
        if (components is null)
            throw new ArgumentNullException(nameof(components));
        if (string.IsNullOrWhiteSpace(owner))
            throw new ArgumentException("The owner must name the protocol that sets the components.", nameof(owner));
        if (healthProbeFactory is null)
            throw new ArgumentNullException(nameof(healthProbeFactory));

        // 守卫：协议拥有的属性不能已经被调用方设置。顺序即文档里的顺序，同时冲突多项时报最靠前的那一个。
        RequireUnset(components.FrameCodec, nameof(ChannelComponents.FrameCodec), owner);
        RequireUnset(components.Correlation, nameof(ChannelComponents.Correlation), owner);
        RequireUnset(components.KeyExtractor, nameof(ChannelComponents.KeyExtractor), owner);
        if (initializer is not null)
            RequireUnset(components.Initializer, nameof(ChannelComponents.Initializer), owner);
        RequireUnset(components.HealthProbe, nameof(ChannelComponents.HealthProbe), owner);
        RequireUnset(components.HealthProbeFactory, nameof(ChannelComponents.HealthProbeFactory), owner);

        // 全部通过才赋值：失败的路径不会留下半配置的组件。
        components.FrameCodec = frameCodec;
        components.Correlation = correlation;
        components.KeyExtractor = keyExtractor;
        if (initializer is not null)
            components.Initializer = initializer;
        components.HealthProbeFactory = healthProbeFactory;
        return components;
    }

    private static void RequireUnset(object? current, string property, string owner)
    {
        if (current is not null)
            throw new ArgumentException(string.Format(AlreadySetFormat, property, owner), "components");
    }
}