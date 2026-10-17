using Junevy.Communication.WebApi.Server.Config;

namespace Junevy.Communication.WebApi.Server.Tests;

/// <summary>监听所有网卡却没有认证的判定（P32-D9）：纯函数，Warning 本身在启动时断言（Task 6）。</summary>
public sealed class ServerExposureRulesTests
{
    [Theory]
    [InlineData("http://*:8080")]
    [InlineData("http://+:8080")]
    [InlineData("http://0.0.0.0:8080")]
    [InlineData("https://[::]:8443")]
    [InlineData("HTTP://*:0")]
    public void WildcardAddress_WithoutAuthentication_IsUnprotected(string url)
    {
        Assert.True(ServerExposureRules.IsUnprotectedWildcard(new[] { url }, authenticated: false));
    }

    [Theory]
    [InlineData("http://localhost:8080")]
    [InlineData("http://127.0.0.1:0")]
    [InlineData("http://[::1]:8080")]
    [InlineData("http://192.168.1.10:8080")]
    public void LoopbackOrSpecificAddress_IsNotFlagged(string url)
    {
        Assert.False(ServerExposureRules.IsUnprotectedWildcard(new[] { url }, authenticated: false));
    }

    [Fact]
    public void WildcardAddress_WithAuthentication_IsNotFlagged()
    {
        Assert.False(ServerExposureRules.IsUnprotectedWildcard(new[] { "http://*:8080" }, authenticated: true));
    }

    [Fact]
    public void AnyWildcardAmongSeveralAddresses_IsFlagged()
    {
        Assert.True(ServerExposureRules.IsUnprotectedWildcard(new[] { "http://127.0.0.1:0", "http://*:8081" }, authenticated: false));
    }
}
