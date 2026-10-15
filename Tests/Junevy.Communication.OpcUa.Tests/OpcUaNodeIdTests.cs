using Junevy.Communication.OpcUa;
using Junevy.Communication.OpcUa.Sdk;
using Opc.Ua;
using Xunit;

namespace Junevy.Communication.OpcUa.Tests;

/// <summary>
/// NodeId 文本解析的每一种合法形式与几种非法形式（P62-D5）。
/// 不需要服务器：命名空间表由测试自己构造。
/// </summary>
public sealed class OpcUaNodeIdTests
{
    private static readonly NamespaceTable Namespaces =
        new(new[] { "http://opcfoundation.org/UA/", "urn:junevy:opcua:test" });

    [Theory]
    [InlineData("i=2258", 0, "2258")]
    [InlineData("ns=2;s=Line1.Speed", 2, "Line1.Speed")]
    [InlineData("ns=2;i=1001", 2, "1001")]
    [InlineData("ns=1;g=11112222-3333-4444-5555-666677778888", 1, "11112222-3333-4444-5555-666677778888")]
    [InlineData("ns=1;b=AQI=", 1, "AQI=")]
    [InlineData("s=Line1.Speed", 0, "Line1.Speed")]
    [InlineData("b=AQI=", 0, "AQI=")]
    [InlineData("nsu=urn:junevy:opcua:test;s=Counter", 1, "Counter")]
    [InlineData("nsu=urn:junevy:opcua:test;i=5", 1, "5")]
    [InlineData("nsu=http://opcfoundation.org/UA/;i=2258", 0, "2258")]
    public void TryParse_EveryLegalForm_Parses(string text, int namespaceIndex, string identifier)
    {
        Assert.True(OpcUaSdkNodeId.TryParse(text, Namespaces, out NodeId nodeId, out string? reason), reason);
        Assert.Equal((ushort)namespaceIndex, nodeId.NamespaceIndex);
        Assert.Equal(identifier, nodeId.IdentifierAsString);
    }

    [Theory]
    [InlineData("nsu=urn:unknown:s=X")]
    [InlineData("ns=;s=x")]
    [InlineData("ns=abc;s=x")]
    [InlineData("i=notanumber")]
    [InlineData("i=99999999999999999999")]
    [InlineData("sg=abc")]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void TryParse_IllegalText_IsRejectedWithAReason(string? text)
    {
        Assert.False(OpcUaSdkNodeId.TryParse(text, Namespaces, out NodeId nodeId, out string? reason));
        Assert.True(nodeId.IsNull);
        Assert.False(string.IsNullOrWhiteSpace(reason));
    }

    [Fact]
    public void TryParse_EmptyText_IsRejectedByThisLibraryNotByTheSdk()
    {
        // SDK 的 NodeId.Parse("") 会成功并给出 i=0；P62-D5 要求"语法错误"包含空串。
        Assert.Equal("i=0", NodeId.Parse("").ToString());

        Assert.False(OpcUaSdkNodeId.TryParse("", Namespaces, out _, out string? reason));
        Assert.Contains("empty", reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void TryParse_NamespaceUriFormWithoutASession_IsRejected()
    {
        Assert.False(OpcUaSdkNodeId.TryParse("nsu=urn:junevy:opcua:test;s=Counter", null, out _, out string? reason));
        Assert.Contains("namespace table", reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void TryParse_ReasonQuotesTheOffendingText()
    {
        Assert.False(OpcUaSdkNodeId.TryParse("i=notanumber", Namespaces, out _, out string? reason));
        Assert.Contains("i=notanumber", reason, StringComparison.Ordinal);
    }

    [Fact]
    public void ToString_RoundTripsTheParsedText()
    {
        Assert.True(OpcUaSdkNodeId.TryParse("ns=2;s=Line1.Speed", Namespaces, out NodeId nodeId, out _));

        Assert.Equal("ns=2;s=Line1.Speed", nodeId.ToString());
    }

    [Fact]
    public void ToString_NamespaceUriFormComesBackAsTheIndexForm()
    {
        Assert.True(OpcUaSdkNodeId.TryParse("nsu=urn:junevy:opcua:test;s=Counter", Namespaces, out NodeId nodeId, out _));

        Assert.Equal("ns=1;s=Counter", nodeId.ToString());
    }
}
