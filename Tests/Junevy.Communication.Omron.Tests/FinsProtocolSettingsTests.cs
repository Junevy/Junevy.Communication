using System.Text;
using Junevy.Communication.Core.Buffers;
using Junevy.Communication.Omron.Protocol;
using Junevy.Communication.Plc;

namespace Junevy.Communication.Omron.Tests;

/// <summary>
/// 连接参数快照（P4-D18）：客户端节点号只接受"0 自动分配"与"1–254 固定节点号"，255 是 FINS 的广播地址；
/// 网络号与单元号是字节字段，取值全部合法。数据格式默认 CDAB + ASCII（P4-D11），快照之后修改选项对象不影响它。
/// </summary>
public sealed class FinsProtocolSettingsTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(254)]
    public void Create_AcceptsEveryClientNodeThatIsNotTheBroadcastAddress(byte clientNode)
    {
        FinsProtocolSettings settings = FinsProtocolSettings.Create(clientNode, 0, 0);

        Assert.Equal(clientNode, settings.ClientNode);
    }

    [Fact]
    public void Create_RejectsClientNode255BecauseItIsTheFinsBroadcastAddress()
    {
        ArgumentOutOfRangeException exception = Assert.Throws<ArgumentOutOfRangeException>(() => FinsProtocolSettings.Create(255, 0, 0));

        Assert.Equal("clientNode", exception.ParamName);
        Assert.Contains("broadcast", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void DataFormat_DefaultsToCdabWordOrderWithAscii()
    {
        FinsProtocolSettings settings = FinsProtocolSettings.Create(0, 0, 0);

        // P4-D11：欧姆龙的 32 位数低字在前、每个字内部大端，规范化布局即设备内存顺序。
        Assert.Equal(WordOrder.CDAB, settings.DataFormat.WordOrder);
        Assert.Equal(Encoding.ASCII, settings.DataFormat.StringEncoding);
    }

    [Fact]
    public void DataFormat_FromTheArgument_IsCarriedThrough()
    {
        var format = new PlcDataFormat(WordOrder.ABCD, Encoding.UTF8);

        FinsProtocolSettings settings = FinsProtocolSettings.Create(0, 0, 0, format);

        Assert.Same(format, settings.DataFormat);
    }

    [Fact]
    public void From_SnapshotsTheOptionsAndAppliesTheDefaultFormat()
    {
        var options = new FinsClientOptions { ClientNode = 11, DestinationNetwork = 1, DestinationUnit = 2 };
        FinsProtocolSettings settings = FinsProtocolSettings.From(options);

        options.ClientNode = 20;
        options.DestinationNetwork = 9;
        options.DestinationUnit = 9;

        Assert.Equal(11, settings.ClientNode);
        Assert.Equal(1, settings.DestinationNetwork);
        Assert.Equal(2, settings.DestinationUnit);
        Assert.Equal(WordOrder.CDAB, settings.DataFormat.WordOrder);
        Assert.Equal(Encoding.ASCII, settings.DataFormat.StringEncoding);
    }

    [Fact]
    public void From_CarriesTheOptionsDataFormat()
    {
        var format = new PlcDataFormat(WordOrder.BADC);

        FinsProtocolSettings settings = FinsProtocolSettings.From(new FinsClientOptions { DataFormat = format });

        Assert.Same(format, settings.DataFormat);
    }

    [Fact]
    public void From_RejectsAFixedClientNodeOf255()
    {
        // 选项是字节字段，只有广播地址需要拒绝；0 与 1–254 都在 P4-D18 的范围内。
        Assert.Throws<ArgumentOutOfRangeException>(() => FinsProtocolSettings.From(new FinsClientOptions { ClientNode = 255 }));
    }

    [Fact]
    public void From_WithNullOptions_IsAnArgumentNullException()
    {
        Assert.Throws<ArgumentNullException>(() => FinsProtocolSettings.From(null!));
    }

    // P4-D18 的固定路由字节：网络号与单元号取自选项，节点号要等握手，因此快照只存这三项。
    [Fact]
    public void Route_WithTheHandshakedNodes_UsesTheOptionNetworkAndUnit()
    {
        FinsProtocolSettings settings = FinsProtocolSettings.Create(0, 3, 4);

        FinsRoute route = settings.RouteWith(new FinsNodeNumbers(0x0B, 0x01));

        Assert.Equal(3, route.DestinationNetwork);
        Assert.Equal(4, route.DestinationUnit);
    }
}
