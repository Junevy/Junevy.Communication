using Junevy.Communication.Channels;
using Junevy.Communication.Channels.Framing;
using Junevy.Communication.Modbus.Framing;
using Junevy.Communication.Plc;

namespace Junevy.Communication.Modbus;

/// <summary>
/// Modbus 的协议配置（P7-D7、D9）：按帧格式与交付方式、介质，把分帧、关联、键提取器与心跳探测填入通道组件，返回同一实例。
/// 帧格式与介质、交付方式的规则只在这里检查，并且在 <see cref="PlcComponentWiring.Apply{TComponents}"/> 之前完成。
/// </summary>
internal static class ModbusProtocolProfile
{
    private const byte MinimumRtuUnitId = 1;
    private const byte MaximumRtuUnitId = 247;

    /// <summary>
    /// 为 Modbus 配置通道组件：MBAP 与 RTU 的分帧，关联（MBAP 为 Keyed，RTU 为 Sequential），以及读 HR0 的心跳探测。
    /// </summary>
    /// <typeparam name="TComponents">通道组件类型。</typeparam>
    /// <param name="components">要配置的组件。</param>
    /// <param name="settings">选项快照。</param>
    /// <param name="delivery">通道的交付方式。</param>
    /// <param name="medium">通道的介质。</param>
    /// <returns>同一组件实例。</returns>
    /// <exception cref="ArgumentNullException"><paramref name="components"/> 或 <paramref name="settings"/> 为 null。</exception>
    /// <exception cref="ArgumentOutOfRangeException">交付方式或介质未定义；RTU 的站号不在 1–247 之内。</exception>
    /// <exception cref="ArgumentException">串口上使用 MBAP；或组件中的分帧、关联、键提取器或心跳探测已由调用方设置。</exception>
    public static TComponents Configure<TComponents>(TComponents components, ModbusProtocolSettings settings, ChannelDelivery delivery, ChannelMedium medium)
        where TComponents : ChannelComponents
    {
        if (components is null)
            throw new ArgumentNullException(nameof(components));
        if (settings is null)
            throw new ArgumentNullException(nameof(settings));
        if (!Enum.IsDefined(typeof(ChannelDelivery), delivery))
            throw new ArgumentOutOfRangeException(nameof(delivery), delivery, "The channel delivery is not defined.");
        if (!Enum.IsDefined(typeof(ChannelMedium), medium))
            throw new ArgumentOutOfRangeException(nameof(medium), medium, "The channel medium is not defined.");

        ModbusFraming framing = ResolveFraming(settings.Framing, medium);
        if (framing == ModbusFraming.Mbap && medium == ChannelMedium.Serial)
            throw new ArgumentException("Serial transports carry RTU frames only; set Framing to Rtu.", nameof(settings));
        if (framing == ModbusFraming.Rtu && (settings.UnitId < MinimumRtuUnitId || settings.UnitId > MaximumRtuUnitId))
        {
            throw new ArgumentOutOfRangeException(nameof(settings), settings.UnitId,
                $"The RTU unit ID must be between {MinimumRtuUnitId} and {MaximumRtuUnitId}.");
        }

        bool keyed = framing == ModbusFraming.Mbap;
        byte unitId = settings.UnitId;
        return PlcComponentWiring.Apply(
            components,
            owner: nameof(ModbusProtocolProfile),
            frameCodec: FrameCodecFor(framing, delivery),
            correlation: keyed ? CorrelationMode.Keyed : CorrelationMode.Sequential,
            keyExtractor: keyed ? new ModbusTransactionKeyExtractor() : null,
            initializer: null,
            healthProbeFactory: channel => new ModbusReadProbe(channel, framing, unitId));
    }

    // 未指定帧格式时按介质决定：串口为 RTU，网络为 MBAP。
    internal static ModbusFraming ResolveFraming(ModbusFraming? requested, ChannelMedium medium)
        => requested ?? (medium == ChannelMedium.Serial ? ModbusFraming.Rtu : ModbusFraming.Mbap);

    // 字节流需要分帧；数据报一个数据报就是一帧，不设分帧。
    private static IFrameCodecFactory? FrameCodecFor(ModbusFraming framing, ChannelDelivery delivery)
    {
        if (delivery == ChannelDelivery.Datagram)
            return null;

        return framing == ModbusFraming.Mbap
            ? FrameCodecFactory.Create(ModbusStreamFraming.MbapOptions())
            : ModbusStreamFraming.RtuCodecs();
    }
}
