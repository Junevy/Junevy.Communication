using static Junevy.Communication.Siemens.Tests.S7TestSupport;

namespace Junevy.Communication.Siemens.Tests;

/// <summary>
/// 连接参数快照（计划第 12 节第 3 条、P4-D19）：连接类型 1–3、Rack 0–7、槽号 0–31 的范围校验集中在这一处，
/// Task 9 的 <c>S7ClientOptions</c> 快照复用同一套规则。
/// </summary>
public sealed class S7ProtocolSettingsTests
{
    [Theory]
    [InlineData(Pg, 0, 1)]
    [InlineData(Op, 7, 31)]
    [InlineData(Basic, 0, 0)]
    [InlineData(Basic, 7, 31)]
    public void Create_AcceptsEveryValueInsideP4_D19(int connectionType, int rack, int slot)
    {
        S7ProtocolSettings settings = S7ProtocolSettings.Create(connectionType, rack, slot);

        Assert.Equal(connectionType, settings.ConnectionType);
        Assert.Equal(rack, settings.Rack);
        Assert.Equal(slot, settings.Slot);
    }

    [Theory]
    [InlineData(0, Rack, Slot)]
    [InlineData(4, Rack, Slot)]
    [InlineData(-1, Rack, Slot)]
    [InlineData(Pg, -1, Slot)]
    [InlineData(Pg, 8, Slot)]
    [InlineData(Pg, Rack, -1)]
    [InlineData(Pg, Rack, 32)]
    public void Create_RejectsValueOutsideP4_D19(int connectionType, int rack, int slot)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => S7ProtocolSettings.Create(connectionType, rack, slot));
    }
}
