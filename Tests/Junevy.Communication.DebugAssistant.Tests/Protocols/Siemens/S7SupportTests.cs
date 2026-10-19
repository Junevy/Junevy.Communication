using Junevy.Communication.DebugAssistant.Protocols.Siemens;
using Junevy.Communication.Siemens;

namespace Junevy.Communication.DebugAssistant.Tests.Protocols.Siemens;

/// <summary>
/// S7 的支持类型（计划 Task 10 第 2 条）：TSAP 计算的三条向量，PDU 换算出的单包上限（240 与 960），String 与 WString 的长度范围校验，
/// 以及字符串头部的读取。
/// </summary>
public sealed class S7SupportTests
{
    [Theory]
    [InlineData(S7ConnectionType.PG, 0, 1, 0x0101)]
    [InlineData(S7ConnectionType.OP, 0, 2, 0x0202)]
    [InlineData(S7ConnectionType.Basic, 2, 5, 0x0345)]
    public void RemoteTsap_FollowsTheFormula(S7ConnectionType connectionType, int rack, int slot, ushort expected) =>
        Assert.Equal(expected, S7Tsap.Remote(connectionType, rack, slot));

    [Fact]
    public void LocalTsap_IsTheSnap7Convention() => Assert.Equal((ushort)0x0100, S7Tsap.Local);

    [Theory]
    [InlineData(8, 1)]
    [InlineData(-1, 1)]
    [InlineData(0, 32)]
    [InlineData(0, -1)]
    public void RackAndSlot_OutsideTheirRanges_AreReported(int rack, int slot) =>
        Assert.NotNull(S7Tsap.Problem(rack, slot));

    [Theory]
    [InlineData(0, 0)]
    [InlineData(7, 31)]
    public void RackAndSlot_AtTheLimits_AreAccepted(int rack, int slot) =>
        Assert.Null(S7Tsap.Problem(rack, slot));

    [Theory]
    [InlineData(240, 222, 212, 111, 106)]
    [InlineData(960, 942, 932, 471, 466)]
    public void PacketLimits_FollowThePduSize(int pduSize, int readBytes, int writeBytes, int readWords, int writeWords) =>
        Assert.Equal(new S7PduLimit(readBytes, writeBytes, readWords, writeWords), S7PduLimits.For(pduSize));

    [Theory]
    [InlineData(S7StringKind.String, 0, false)]
    [InlineData(S7StringKind.String, 1, true)]
    [InlineData(S7StringKind.String, 254, true)]
    [InlineData(S7StringKind.String, 255, false)]
    [InlineData(S7StringKind.WString, 0, false)]
    [InlineData(S7StringKind.WString, 1, true)]
    [InlineData(S7StringKind.WString, 16382, true)]
    [InlineData(S7StringKind.WString, 16383, false)]
    public void StringMaxLength_IsCheckedAgainstTheKindsRange(S7StringKind kind, int maxLength, bool accepted) =>
        Assert.Equal(accepted, S7StringLimits.Problem(kind, maxLength) is null);

    [Fact]
    public void StringHeader_IsReadFromBothKinds()
    {
        Assert.Equal((2, 4), (S7StringLimits.HeaderBytes(S7StringKind.String), S7StringLimits.HeaderBytes(S7StringKind.WString)));
        Assert.Equal((20, 5), S7StringLimits.ReadHeader(S7StringKind.String, [20, 5]));
        Assert.Equal((120, 5), S7StringLimits.ReadHeader(S7StringKind.WString, [0x00, 0x78, 0x00, 0x05]));
    }
}
