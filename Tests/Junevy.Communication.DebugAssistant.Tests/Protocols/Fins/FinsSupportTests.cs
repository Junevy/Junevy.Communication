using Junevy.Communication.Channels;
using Junevy.Communication.Core.Results;
using Junevy.Communication.DebugAssistant.Protocols.Fins;
using Junevy.Communication.DebugAssistant.Tests.Support;
using Junevy.Communication.Omron;
using Junevy.Communication.Plc;
using Junevy.Communication.Tcp;

namespace Junevy.Communication.DebugAssistant.Tests.Protocols.Fins;

/// <summary>
/// FINS 的支持类型（计划 Task 10 第 2 条）：节点参数的范围（255 被拒绝），区域速查表（18 项）与 <c>ParseAddress</c> 的一致性，
/// 区域代码的十六进制写法。
/// </summary>
public sealed class FinsSupportTests
{
    public static IEnumerable<object[]> Areas => FinsAreas.All.Select(area => new object[] { area });

    [Fact]
    public void AreaTable_HasEighteenAreas_WithUniqueSymbols()
    {
        Assert.Equal(18, FinsAreas.All.Count);
        Assert.Equal(18, FinsAreas.All.Select(area => area.Symbol).Distinct().Count());
    }

    [Theory]
    [MemberData(nameof(Areas))]
    public void AreaTable_AgreesWithParseAddress(FinsAreaEntry area)
    {
        using IPlcClient client = FinsClient.Create(new TcpClientChannelConfig { Host = "127.0.0.1", Port = 9600 }, new ScriptedTransports().Tcp, new FinsClientOptions());

        CommResult<PlcAddress> parsed = client.ParseAddress(area.Symbol + "0");

        Assert.True(parsed.IsSuccess, parsed.ErrorMessage);
        FinsAddress address = Assert.IsType<FinsAddress>(parsed.Data);
        Assert.Equal((area.Symbol, area.WordAreaCode, area.BitAreaCode), (address.Device.Symbol, address.Device.WordAreaCode, address.Device.BitAreaCode));
    }

    [Theory]
    [InlineData("CIO", "0xB0", "0x30")]
    [InlineData("D", "0x82", "0x02")]
    [InlineData("E0_", "0xA0", "0x20")]
    [InlineData("EC_", "0xAC", "0x2C")]
    public void AreaCodes_AreShownInHex(string symbol, string wordCode, string bitCode)
    {
        FinsAreaEntry area = FinsAreas.All.Single(entry => entry.Symbol == symbol);

        Assert.Equal((wordCode, bitCode), (area.WordAreaCodeText, area.BitAreaCodeText));
    }

    [Theory]
    [InlineData(0, null)]
    [InlineData(1, null)]
    [InlineData(254, null)]
    [InlineData(-1, "客户端节点必须在 0 到 254 之间（0 表示由 CPU 分配）。")]
    [InlineData(256, "客户端节点必须在 0 到 254 之间（0 表示由 CPU 分配）。")]
    public void ClientNode_IsZeroOrOneToTwoHundredFiftyFour(int clientNode, string? message) =>
        Assert.Equal(message, FinsNodes.ClientNodeProblem(clientNode));

    [Fact]
    public void ClientNode_255_IsTheBroadcastAddress_AndIsRejected() =>
        Assert.Contains("255", FinsNodes.ClientNodeProblem(255) ?? string.Empty);

    [Theory]
    [InlineData(0, true)]
    [InlineData(255, true)]
    [InlineData(256, false)]
    [InlineData(-1, false)]
    public void NetworkAndUnit_AreBytes(int value, bool accepted) =>
        Assert.Equal(accepted, FinsNodes.ByteProblem("目的网络号", value) is null);
}
