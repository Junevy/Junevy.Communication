using Junevy.Communication.Channels;
using Junevy.Communication.Channels.Framing;
using Junevy.Communication.Core.Buffers;
using Junevy.Communication.Core.Results;
using Junevy.Communication.Modbus.Framing;
using Junevy.Communication.Testing;

namespace Junevy.Communication.Modbus.Tests.Options;

/// <summary>
/// 协议配置（P7-D7、D9）：帧格式与交付方式、介质的规则表；组件的分帧、关联与键提取器；站号的范围；组件已被设置时的守卫。
/// </summary>
public sealed class ModbusProtocolProfileTests
{
    private static ChannelComponents Configure(ModbusFraming? framing, ChannelDelivery delivery, ChannelMedium medium, byte unitId = 1)
    {
        var settings = ModbusProtocolSettings.From(new ModbusClientOptions { Framing = framing, UnitId = unitId });
        return ModbusProtocolProfile.Configure(new ChannelComponents(), settings, delivery, medium);
    }

    [Fact(Timeout = 20000)]
    public void SerialWithoutAFramingChoice_IsRtuWithSequentialCorrelation()
    {
        ChannelComponents components = Configure(null, ChannelDelivery.Stream, ChannelMedium.Serial);

        Assert.NotNull(components.FrameCodec);
        Assert.IsType<ModbusRtuFrameCodecFactory>(components.FrameCodec);
        Assert.Equal(CorrelationMode.Sequential, components.Correlation);
        Assert.Null(components.KeyExtractor);
    }

    [Fact(Timeout = 20000)]
    public void NetworkStreamRtu_IsRtuOverTcpWithSequentialCorrelation()
    {
        ChannelComponents components = Configure(ModbusFraming.Rtu, ChannelDelivery.Stream, ChannelMedium.Network);

        Assert.IsType<ModbusRtuFrameCodecFactory>(components.FrameCodec);
        Assert.Equal(CorrelationMode.Sequential, components.Correlation);
        Assert.Null(components.KeyExtractor);
    }

    [Fact(Timeout = 20000)]
    public void NetworkStreamMbap_IsKeyedWithTheTransactionKey()
    {
        ChannelComponents components = Configure(ModbusFraming.Mbap, ChannelDelivery.Stream, ChannelMedium.Network);

        Assert.NotNull(components.FrameCodec);
        Assert.Equal(CorrelationMode.Keyed, components.Correlation);
        Assert.IsType<ModbusTransactionKeyExtractor>(components.KeyExtractor);
    }

    [Fact(Timeout = 20000)]
    public void DatagramRtu_HasNoFramingAndSequentialCorrelation()
    {
        ChannelComponents components = Configure(ModbusFraming.Rtu, ChannelDelivery.Datagram, ChannelMedium.Network);

        Assert.Null(components.FrameCodec);
        Assert.Equal(CorrelationMode.Sequential, components.Correlation);
        Assert.Null(components.KeyExtractor);
    }

    [Fact(Timeout = 20000)]
    public void DatagramMbap_HasNoFramingAndKeyedCorrelation()
    {
        ChannelComponents components = Configure(ModbusFraming.Mbap, ChannelDelivery.Datagram, ChannelMedium.Network);

        Assert.Null(components.FrameCodec);
        Assert.Equal(CorrelationMode.Keyed, components.Correlation);
        Assert.IsType<ModbusTransactionKeyExtractor>(components.KeyExtractor);
    }

    [Fact(Timeout = 20000)]
    public void DatagramWithoutAFramingChoice_OnNetworkIsMbap()
    {
        ChannelComponents components = Configure(null, ChannelDelivery.Datagram, ChannelMedium.Network);

        Assert.Equal(CorrelationMode.Keyed, components.Correlation);
    }

    [Theory(Timeout = 20000)]
    [InlineData(ChannelDelivery.Stream)]
    [InlineData(ChannelDelivery.Datagram)]
    public void MbapOnSerial_IsAnArgumentException(ChannelDelivery delivery)
    {
        var settings = ModbusProtocolSettings.From(new ModbusClientOptions { Framing = ModbusFraming.Mbap });

        Assert.Throws<ArgumentException>(() => ModbusProtocolProfile.Configure(new ChannelComponents(), settings, delivery, ChannelMedium.Serial));
    }

    [Fact(Timeout = 20000)]
    public void UndefinedFraming_IsAnOutOfRangeException()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => Configure((ModbusFraming)99, ChannelDelivery.Stream, ChannelMedium.Network));
    }

    [Fact(Timeout = 20000)]
    public void UndefinedDeliveryOrMedium_IsAnOutOfRangeException()
    {
        var settings = ModbusProtocolSettings.From(new ModbusClientOptions());

        Assert.Throws<ArgumentOutOfRangeException>(() => ModbusProtocolProfile.Configure(new ChannelComponents(), settings, (ChannelDelivery)99,
            ChannelMedium.Network));
        Assert.Throws<ArgumentOutOfRangeException>(() => ModbusProtocolProfile.Configure(new ChannelComponents(), settings,
            ChannelDelivery.Stream, (ChannelMedium)99));
    }

    [Theory(Timeout = 20000)]
    [InlineData(ModbusFraming.Rtu, 0)]
    [InlineData(ModbusFraming.Rtu, 248)]
    [InlineData(ModbusFraming.Rtu, 255)]
    public void RtuUnitIdOutsideOneToTwoHundredFortySeven_IsAnOutOfRangeException(ModbusFraming framing, byte unitId)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => Configure(framing, ChannelDelivery.Stream, ChannelMedium.Serial, unitId));
    }

    [Theory(Timeout = 20000)]
    [InlineData(ModbusFraming.Rtu, 1)]
    [InlineData(ModbusFraming.Rtu, 247)]
    [InlineData(ModbusFraming.Mbap, 0)]
    [InlineData(ModbusFraming.Mbap, 255)]
    public void UnitIdInTheValidRangeForTheFraming_IsAccepted(ModbusFraming framing, byte unitId)
    {
        ChannelComponents components = Configure(framing, ChannelDelivery.Stream, ChannelMedium.Network, unitId);

        Assert.NotNull(components.HealthProbeFactory);
    }

    [Fact(Timeout = 20000)]
    public void ComponentsAlreadySetByTheCaller_AreAnArgumentException()
    {
        var components = new ChannelComponents { FrameCodec = new ModbusRtuFrameCodecFactory() };
        var settings = ModbusProtocolSettings.From(new ModbusClientOptions { Framing = ModbusFraming.Rtu });

        ArgumentException error = Assert.Throws<ArgumentException>(() => ModbusProtocolProfile.Configure(components, settings,
            ChannelDelivery.Stream, ChannelMedium.Serial));

        Assert.Contains("FrameCodec", error.Message);
    }

    [Fact(Timeout = 20000)]
    public void HealthProbeFactory_CreatesTheModbusReadProbe()
    {
        ChannelComponents components = Configure(ModbusFraming.Rtu, ChannelDelivery.Stream, ChannelMedium.Serial);
        var channel = new ScriptedByteChannel(_ => CommResult<byte[]>.Fail("unused", CommErrorKind.Unspecified));

        IHealthProbe probe = components.HealthProbeFactory!(channel);

        Assert.IsType<ModbusReadProbe>(probe);
    }

    [Fact(Timeout = 20000)]
    public void Settings_DefaultDataFormatIsWordOrderAbcdWithAsciiStrings()
    {
        ModbusProtocolSettings settings = ModbusProtocolSettings.From(new ModbusClientOptions());

        Assert.Equal(WordOrder.ABCD, settings.DataFormat.WordOrder);
        Assert.Null(settings.Framing);
        Assert.Equal((byte)1, settings.UnitId);
    }

    [Fact(Timeout = 20000)]
    public void Settings_NullOptions_IsAnArgumentNullException()
    {
        Assert.Throws<ArgumentNullException>(() => ModbusProtocolSettings.From(null!));
    }

    [Fact(Timeout = 20000)]
    public void Settings_UndefinedFraming_IsAnOutOfRangeException()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => ModbusProtocolSettings.From(new ModbusClientOptions { Framing = (ModbusFraming)99 }));
    }
}
