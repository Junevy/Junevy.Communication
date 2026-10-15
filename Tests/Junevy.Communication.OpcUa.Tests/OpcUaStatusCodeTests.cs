using Junevy.Communication.OpcUa;
using Xunit;

namespace Junevy.Communication.OpcUa.Tests;

/// <summary>
/// 状态码的三类判断、名称与相等性（计划第 6 节）。判定按 OPC UA 规范 Part 4 的最高两位在本地完成，
/// 不复用 SDK 的判定函数——SDK 的 <c>IsUncertain(0xC0000000)</c> 与 <c>IsBad(0xC0000000)</c> 会同时为真。
/// </summary>
public sealed class OpcUaStatusCodeTests
{
    [Theory]
    [InlineData(0x00000000u, true, false, false)]
    [InlineData(0x0000_FFFFu, true, false, false)]
    [InlineData(0x4000_0000u, false, true, false)]
    [InlineData(0x4000_0001u, false, true, false)]
    [InlineData(0x8000_0000u, false, false, true)]
    [InlineData(0x8034_0000u, false, false, true)]
    [InlineData(0xC000_0000u, false, false, true)]
    public void Severity_IsDecidedByTheTopTwoBits(uint code, bool good, bool uncertain, bool bad)
    {
        var status = new OpcUaStatusCode(code);

        Assert.Equal(good, status.IsGood);
        Assert.Equal(uncertain, status.IsUncertain);
        Assert.Equal(bad, status.IsBad);
    }

    [Fact]
    public void Good_HasCodeZero()
        => Assert.Equal(0u, OpcUaStatusCode.Good.Code);

    [Theory]
    [InlineData(0x00000000u, "Good")]
    [InlineData(0x40000000u, "Uncertain")]
    [InlineData(0x80340000u, "BadNodeIdUnknown")]
    [InlineData(0x80740000u, "BadTypeMismatch")]
    [InlineData(0x80260000u, "BadSessionClosed")]
    [InlineData(0x80AE0000u, "BadConnectionClosed")]
    [InlineData(0x803B0000u, "BadNotWritable")]
    public void SymbolicName_ReturnsTheNameOfAKnownCode(uint code, string expected)
        => Assert.Equal(expected, new OpcUaStatusCode(code).SymbolicName);

    [Fact]
    public void SymbolicName_UnknownCode_FallsBackToTheHexadecimalForm()
    {
        // 0x0FFF0000 不在 SDK 的表里；0x80AB0000 是 BadInvalidArgument，不是未知码（计划第 6 节的例子需要更正）。
        var status = new OpcUaStatusCode(0x0FFF0000u);

Assert.Equal("0x0FFF0000", status.SymbolicName);

        // 计划第 6 节举的"未知码" 0x80AB0000 其实有名字，这里一并更正。
        Assert.Equal("BadInvalidArgument", new OpcUaStatusCode(0x80AB0000u).SymbolicName);
    }

    [Fact]
    public void ToString_CombinesTheNameAndTheCode()
    {
        Assert.Equal("BadNodeIdUnknown (0x80340000)", new OpcUaStatusCode(0x80340000u).ToString());
        Assert.Equal("Good (0x00000000)", OpcUaStatusCode.Good.ToString());
        Assert.Equal("0x0FFF0000 (0x0FFF0000)", new OpcUaStatusCode(0x0FFF0000u).ToString());
    }

    [Fact]
    public void Equality_ComparesTheCode()
    {
        var a = new OpcUaStatusCode(0x80340000u);
        var b = new OpcUaStatusCode(0x80340000u);
        var c = new OpcUaStatusCode(0x80330000u);

        Assert.True(a == b);
        Assert.False(a != b);
        Assert.True(a != c);
        Assert.Equal(a.GetHashCode(), b.GetHashCode());
        Assert.False(a.Equals(c));
        Assert.False(a.Equals(null));
        Assert.False(a.Equals("BadNodeIdUnknown"));
    }

    [Fact]
    public void NodeClass_CarriesTheSpecificationBits()
    {
        Assert.Equal(0, (int)OpcUaNodeClass.Unspecified);
        Assert.Equal(1, (int)OpcUaNodeClass.Object);
        Assert.Equal(2, (int)OpcUaNodeClass.Variable);
        Assert.Equal(4, (int)OpcUaNodeClass.Method);
        Assert.Equal(8, (int)OpcUaNodeClass.ObjectType);
        Assert.Equal(16, (int)OpcUaNodeClass.VariableType);
        Assert.Equal(32, (int)OpcUaNodeClass.ReferenceType);
        Assert.Equal(64, (int)OpcUaNodeClass.DataType);
        Assert.Equal(128, (int)OpcUaNodeClass.View);
        Assert.Equal(3, (int)(OpcUaNodeClass.Object | OpcUaNodeClass.Variable));
    }
}
