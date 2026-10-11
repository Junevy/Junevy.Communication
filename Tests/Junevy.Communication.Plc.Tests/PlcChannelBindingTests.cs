using Junevy.Communication.Channels;
using Junevy.Communication.Testing;
using Microsoft.Extensions.Logging.Abstractions;

namespace Junevy.Communication.Plc.Tests;

/// <summary>
/// <see cref="PlcChannelBinding"/> 的契约（P25-D4）：协议按创建器声明的交付方式与介质配置组件；传输配置的精确类型必须与创建器一致；
/// 日志工厂缺省为 <see cref="NullLoggerFactory"/>；参数为 null 时的异常指明参数名。
/// </summary>
public sealed class PlcChannelBindingTests
{
    [Fact]
    public void CreateChannel_PassesTheCreatorsDeliveryAndMediumToTheConfigureFunction()
    {
        var creator = new RecordingChannelCreator(typeof(StubConfig), ChannelDelivery.Datagram, ChannelMedium.Serial);
        ChannelDelivery? delivery = null;
        ChannelMedium? medium = null;

        using IClientChannel channel = PlcChannelBinding.CreateChannel(creator, null, new StubConfig(), (received, receivedMedium) =>
        {
            delivery = received;
            medium = receivedMedium;
            return new ChannelComponents();
        });

        Assert.Equal(ChannelDelivery.Datagram, delivery);
        Assert.Equal(ChannelMedium.Serial, medium);
    }

    [Fact]
    public void CreateChannel_HandsTheNameTransportAndComponentsToTheCreator()
    {
        var transport = new StubConfig();
        var components = new ChannelComponents();
        var creator = new RecordingChannelCreator(typeof(StubConfig), ChannelDelivery.Stream, ChannelMedium.Network);

        using IClientChannel channel = PlcChannelBinding.CreateChannel(creator, "plc-1", transport, (_, _) => components);

        Assert.Equal("plc-1", creator.Name);
        Assert.Same(transport, creator.Config);
        Assert.Same(components, creator.Components);
    }

    [Fact]
    public void CreateChannel_WithATransportOfAnotherType_IsAnArgumentExceptionBeforeConfiguring()
    {
        var creator = new RecordingChannelCreator(typeof(StubConfig), ChannelDelivery.Stream, ChannelMedium.Network);
        bool configured = false;

        Assert.Throws<ArgumentException>(() =>
        {
            PlcChannelBinding.CreateChannel(creator, null, new OtherConfig(), (_, _) =>
            {
                configured = true;
                return new ChannelComponents();
            });
        });

        Assert.False(configured);
        Assert.Equal(0, creator.CreateCount);
    }

    [Fact]
    public void CreateChannel_WithoutALoggerFactory_PassesTheNullLoggerFactoryToTheCreator()
    {
        var creator = new RecordingChannelCreator(typeof(StubConfig), ChannelDelivery.Stream, ChannelMedium.Network);

        using IClientChannel channel = PlcChannelBinding.CreateChannel(creator, null, new StubConfig(), (_, _) => new ChannelComponents());

        Assert.Same(NullLoggerFactory.Instance, creator.LoggerFactory);
    }

    [Theory]
    [InlineData("creator")]
    [InlineData("transport")]
    [InlineData("configureComponents")]
    public void CreateChannel_WithANullArgument_IsAnArgumentNullExceptionNamingIt(string nullArgument)
    {
        var creator = new RecordingChannelCreator(typeof(StubConfig), ChannelDelivery.Stream, ChannelMedium.Network);
        var transport = new StubConfig();

        ArgumentNullException exception = nullArgument switch
        {
            "creator" => Assert.Throws<ArgumentNullException>(() => { PlcChannelBinding.CreateChannel(null!, null, transport, (_, _) => new ChannelComponents()); }),
            "transport" => Assert.Throws<ArgumentNullException>(() => { PlcChannelBinding.CreateChannel(creator, null, null!, (_, _) => new ChannelComponents()); }),
            _ => Assert.Throws<ArgumentNullException>(() => { PlcChannelBinding.CreateChannel(creator, null, transport, null!); }),
        };

        Assert.Equal(nullArgument, exception.ParamName);
    }

    private sealed class StubConfig : IChannelConfig
    {
    }

    private sealed class OtherConfig : IChannelConfig
    {
    }
}
