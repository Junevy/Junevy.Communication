namespace Junevy.Communication.Channels;

/// <summary>
/// 协议组件与传输配置的优先级规则（P25-D3）。传输在校验配置时调用：内置的 TCP 客户端、TCP 服务端、UDP 与串口，
/// 以及派生 <see cref="StreamClientChannel"/> 的第三方传输。规则只覆盖"同名设置"（分帧与关联）；
/// 心跳、重连等补充型组件不在此列，它们与配置的关系不变。
/// </summary>
public static class ComponentPrecedence
{
    private const string ConfigParameterName = "config";

    private const string FramingConflictMessage =
        "Framing is defined by ChannelComponents.FrameCodec; leave the configuration's Framing at its default.";

    private const string CorrelationConflictMessage =
        "Correlation is defined by ChannelComponents.Correlation; leave the configuration's Correlation at its default or set it to the same value.";

    /// <summary>
    /// 校验协议组件与传输配置中的同名设置。冲突时抛出 <see cref="ArgumentException"/>（参数名为 <c>config</c>），构造期间即失败，不会静默以组件为准。
    /// </summary>
    /// <param name="components">协议组件；为 null 时不检查。</param>
    /// <param name="framing">配置中的分帧；传输没有分帧设置（数据报）时传 null。</param>
    /// <param name="defaultFraming">该传输配置的默认分帧（例如 <c>new SerialChannelConfig().Framing</c>）；<paramref name="framing"/> 为 null 时传 null。</param>
    /// <param name="correlation">配置中的关联模式。</param>
    /// <param name="defaultCorrelation">该传输配置的默认关联模式。</param>
    /// <exception cref="ArgumentException">组件设置了 FrameCodec 而 framing 与 defaultFraming 的值不同；或组件设置了 Correlation，而 correlation 既不等于 defaultCorrelation 也不等于组件的值。</exception>
    public static void Validate(ChannelComponents? components, FramingOptions? framing, FramingOptions? defaultFraming,
                                CorrelationMode correlation, CorrelationMode defaultCorrelation)
    {
        if (components == null)
            return;

        if (components.FrameCodec != null && framing != null && !framing.HasSameValues(defaultFraming))
            throw new ArgumentException(FramingConflictMessage, ConfigParameterName);

        if (components.Correlation is { } componentCorrelation
            && correlation != defaultCorrelation && correlation != componentCorrelation)
            throw new ArgumentException(CorrelationConflictMessage, ConfigParameterName);
    }
}
