using System.Net.Http;
using Junevy.Communication.Core.Resilience;

namespace Junevy.Communication.WebApi.Tests;

/// <summary>
/// <see cref="WebApiComponents"/> 的默认值与可替换项测试。
/// </summary>
public sealed class WebApiComponentsTests
{
    [Fact]
    public void Defaults_HaveNoInterceptorsBackoffOrHandler()
    {
        var components = new WebApiComponents();

        Assert.Empty(components.Interceptors);
        Assert.Null(components.RetryBackoff);
        Assert.Null(components.HttpHandler);
    }

    [Fact]
    public void Properties_CanBeSet()
    {
        var backoff = new FixedIntervalBackoff(10);
        using var handler = new HttpClientHandler();
        var components = new WebApiComponents { RetryBackoff = backoff, HttpHandler = handler };

        components.Interceptors.Add(new TestInterceptor());

        Assert.Single(components.Interceptors);
        Assert.Same(backoff, components.RetryBackoff);
        Assert.Same(handler, components.HttpHandler);
    }

    private sealed class TestInterceptor : WebApiInterceptor
    {
    }
}
