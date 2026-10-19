using Junevy.Communication.DebugAssistant.Protocols.OpcUa;
using Junevy.Communication.DebugAssistant.Sessions;
using Junevy.Communication.OpcUa;

namespace Junevy.Communication.DebugAssistant.Tests.Protocols.OpcUa;

/// <summary>
/// OPC UA 页的纯逻辑（计划第 7.8 节）：写入类型的解析（15 种与一维数组，逐项转换、不做推断）、值与状态的显示文字、
/// NodeId 写法提示、状态映射。
/// </summary>
public sealed class OpcUaSupportTests
{
    [Fact]
    public void ParseValue_EveryWriteType_ProducesTheNamedDotNetType()
    {
        Assert.Equal((object)true, OpcUaSupport.ParseValue(OpcUaWriteType.Boolean, false, "true"));
        Assert.Equal((object)(sbyte)-7, OpcUaSupport.ParseValue(OpcUaWriteType.SByte, false, "-7"));
        Assert.Equal((object)(byte)7, OpcUaSupport.ParseValue(OpcUaWriteType.Byte, false, "7"));
        Assert.Equal((object)(short)-300, OpcUaSupport.ParseValue(OpcUaWriteType.Int16, false, "-300"));
        Assert.Equal((object)(ushort)300, OpcUaSupport.ParseValue(OpcUaWriteType.UInt16, false, "300"));
        Assert.Equal((object)(-70000), OpcUaSupport.ParseValue(OpcUaWriteType.Int32, false, "-70000"));
        Assert.Equal((object)70000u, OpcUaSupport.ParseValue(OpcUaWriteType.UInt32, false, "70000"));
        Assert.Equal((object)(-5000000000L), OpcUaSupport.ParseValue(OpcUaWriteType.Int64, false, "-5000000000"));
        Assert.Equal((object)5000000000UL, OpcUaSupport.ParseValue(OpcUaWriteType.UInt64, false, "5000000000"));
        Assert.Equal((object)1.25f, OpcUaSupport.ParseValue(OpcUaWriteType.Float, false, "1.25"));
        Assert.Equal((object)2.5, OpcUaSupport.ParseValue(OpcUaWriteType.Double, false, "2.5"));
        Assert.Equal((object)"hi there", OpcUaSupport.ParseValue(OpcUaWriteType.String, false, "hi there"));
        Assert.Equal(
            (object)new DateTime(2026, 1, 2, 3, 4, 5, DateTimeKind.Utc),
            OpcUaSupport.ParseValue(OpcUaWriteType.DateTime, false, "2026-01-02T03:04:05Z"));
        Assert.Equal(
            (object)Guid.Parse("11111111-2222-3333-4444-555555555555"),
            OpcUaSupport.ParseValue(OpcUaWriteType.Guid, false, "11111111-2222-3333-4444-555555555555"));
        Assert.Equal(new byte[] { 0x01, 0xFF }, (byte[])OpcUaSupport.ParseValue(OpcUaWriteType.ByteString, false, "hex: 01 FF"));
    }

    [Fact]
    public void ParseValue_Arrays_OneElementPerLine_AndBlankLinesAreIgnored()
    {
        Assert.Equal(new[] { 1, 2, 3 }, (int[])OpcUaSupport.ParseValue(OpcUaWriteType.Int32, true, "1\n\n2\r\n3"));
        Assert.Equal(new[] { true, false }, (bool[])OpcUaSupport.ParseValue(OpcUaWriteType.Boolean, true, "true\nfalse"));
        Assert.Equal(new[] { "a, b", "c" }, (string[])OpcUaSupport.ParseValue(OpcUaWriteType.String, true, "a, b\nc"));
    }

    [Fact]
    public void ParseValue_EmptyArray_IsRefused()
    {
        var ex = Assert.Throws<FormatException>(() => OpcUaSupport.ParseValue(OpcUaWriteType.Int32, true, "\n\n"));
        Assert.Equal("数组至少需要一个元素（每行一个）。", ex.Message);
    }

    [Fact]
    public void ParseValue_OutOfRange_IsAFormatExceptionNamingTheType()
    {
        var ex = Assert.Throws<FormatException>(() => OpcUaSupport.ParseValue(OpcUaWriteType.Int16, false, "70000"));
        Assert.Equal("无法解析为 Int16 (short)：70000", ex.Message);
    }

    [Fact]
    public void ParseValue_Boolean_AcceptsOneAndZero()
    {
        Assert.Equal((object)true, OpcUaSupport.ParseValue(OpcUaWriteType.Boolean, false, "1"));
        Assert.Equal((object)false, OpcUaSupport.ParseValue(OpcUaWriteType.Boolean, false, "0"));
        Assert.Throws<FormatException>(() => OpcUaSupport.ParseValue(OpcUaWriteType.Boolean, false, "yes"));
    }

    [Fact]
    public void ParseValue_ByteString_IsHex_NotText()
    {
        Assert.Throws<FormatException>(() => OpcUaSupport.ParseValue(OpcUaWriteType.ByteString, false, "text"));
        Assert.Equal(new byte[] { 0x41 }, (byte[])OpcUaSupport.ParseValue(OpcUaWriteType.ByteString, false, "41"));
    }

    [Fact]
    public void FormatValue_ShowsByteStringsInHex_ArraysWithCommas_AndUnsupportedTypesByName()
    {
        Assert.Equal("01 FF", OpcUaSupport.FormatValue(new OpcUaValue(new byte[] { 0x01, 0xFF }, new OpcUaStatusCode(0))));
        Assert.Equal("1, 2, 3", OpcUaSupport.FormatValue(new OpcUaValue(new[] { 1, 2, 3 }, new OpcUaStatusCode(0))));
        Assert.Equal("（不支持的类型：Speed）", OpcUaSupport.FormatValue(new OpcUaValue(null, new OpcUaStatusCode(0), unsupportedType: "Speed")));
        Assert.Equal("—", OpcUaSupport.FormatValue(new OpcUaValue(null, new OpcUaStatusCode(0))));
    }

    [Fact]
    public void DotNetTypeName_NamesScalarsAndArrays()
    {
        Assert.Equal("int", OpcUaSupport.DotNetTypeName(new OpcUaValue(42, new OpcUaStatusCode(0))));
        Assert.Equal("double[]", OpcUaSupport.DotNetTypeName(new OpcUaValue(new[] { 1.5 }, new OpcUaStatusCode(0))));
        Assert.Equal("byte[]", OpcUaSupport.DotNetTypeName(new OpcUaValue(new byte[] { 1 }, new OpcUaStatusCode(0))));
        Assert.Equal("Speed", OpcUaSupport.DotNetTypeName(new OpcUaValue(null, new OpcUaStatusCode(0), unsupportedType: "Speed")));
    }

    [Fact]
    public void Status_IsDescribedWithItsCategoryAndSymbolicName()
    {
        Assert.StartsWith("好：", OpcUaSupport.DescribeStatus(new OpcUaStatusCode(0)));
        Assert.StartsWith("不确定：", OpcUaSupport.DescribeStatus(new OpcUaStatusCode(0x40000000u)));
        Assert.StartsWith("坏：BadNodeIdUnknown", OpcUaSupport.DescribeStatus(new OpcUaStatusCode(0x80340000u)));
        Assert.StartsWith("坏：BadTypeMismatch", OpcUaSupport.DescribeStatus(new OpcUaStatusCode(0x80740000u)));
    }

    [Theory]
    [InlineData("ns=2;s=Line1.Speed", true)]
    [InlineData("NS=2;i=1001", true)]
    [InlineData("nsu=urn:junevy:opcua:test;s=Counter", false)]
    [InlineData("i=85", false)]
    public void NodeIdHint_WarnsOnlyAboutNamespaceIndexes(string nodeId, bool hinted) =>
        Assert.Equal(hinted, OpcUaSupport.NodeIdHint(nodeId).Length > 0);

    [Fact]
    public void Timestamps_UtcBecomeLocal_AndAbsentOnesAreADash()
    {
        Assert.Equal("—", OpcUaSupport.FormatTimestamp(null));
        Assert.Matches(@"^\d{4}-\d{2}-\d{2} \d{2}:\d{2}:\d{2}\.\d{3}$", OpcUaSupport.FormatTimestamp(DateTime.UtcNow));
    }

    [Theory]
    [InlineData(OpcUaSessionState.Disconnected, null, SessionState.NotConnected)]
    [InlineData(OpcUaSessionState.Disconnected, "Session closed by the server.", SessionState.Faulted)]
    [InlineData(OpcUaSessionState.Connecting, null, SessionState.Connecting)]
    [InlineData(OpcUaSessionState.Connected, null, SessionState.Connected)]
    [InlineData(OpcUaSessionState.Reconnecting, "BadConnectionClosed", SessionState.Reconnecting)]
    public void StateMapping_FollowsTheLibrary(OpcUaSessionState state, string? reason, SessionState expected) =>
        Assert.Equal(expected, OpcUaSupport.ToSessionState(state, reason));

    [Fact]
    public void WriteTypes_AreFifteenNamedDotNetTypes()
    {
        Assert.Equal(15, Enum.GetValues<OpcUaWriteType>().Length);
        Assert.Equal("Int16 (short)", OpcUaSupport.WriteTypeName(OpcUaWriteType.Int16));
        Assert.Equal("ByteString (byte[])", OpcUaSupport.WriteTypeName(OpcUaWriteType.ByteString));
    }
}
