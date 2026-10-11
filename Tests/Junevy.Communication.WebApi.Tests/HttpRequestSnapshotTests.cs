using Junevy.Communication.WebApi.Http;

namespace Junevy.Communication.WebApi.Tests;

/// <summary>
/// <see cref="HttpRequestSnapshot"/> 的测试：头的合并顺序（默认头 → 请求头 → 幂等键）与不可变性（P3-D5：离线缓存以后从快照重放）。
/// </summary>
public sealed class HttpRequestSnapshotTests
{
    [Fact]
    public void Create_StoresTheRequestParts()
    {
        EndpointSettings settings = Settings();
        byte[] body = { 1, 2, 3 };

        HttpRequestSnapshot snapshot = HttpRequestSnapshot.Create(settings, "POST", "orders?id=7", null, "application/json", body, "key-1");

        Assert.Equal("POST", snapshot.Method);
        Assert.Equal("orders?id=7", snapshot.RelativeUri);
        Assert.Equal("application/json", snapshot.ContentType);
        Assert.Same(body, snapshot.Body);
        Assert.Equal("key-1", snapshot.IdempotencyKey);
    }

    [Fact]
    public void Create_MergesDefaultHeadersThenRequestHeadersThenTheIdempotencyKey()
    {
        EndpointSettings settings = Settings(new Dictionary<string, string>
        {
            ["X-Default"] = "default",
            ["X-Shared"] = "from-default",
            ["Idempotency-Key"] = "from-default",
        });
        var requestHeaders = new Dictionary<string, string> { ["x-shared"] = "from-request", ["X-Request"] = "request" };

        HttpRequestSnapshot snapshot = HttpRequestSnapshot.Create(settings, "POST", "", requestHeaders, null, Array.Empty<byte>(), "key-1");

        Assert.Equal("default", snapshot.Headers["X-Default"]);
        Assert.Equal("from-request", snapshot.Headers["X-Shared"]);
        Assert.Equal("request", snapshot.Headers["X-Request"]);
        Assert.Equal("key-1", snapshot.Headers["Idempotency-Key"]);
        Assert.Equal(4, snapshot.Headers.Count);
    }

    [Fact]
    public void Create_WithoutAKey_DoesNotAddTheIdempotencyHeader()
    {
        HttpRequestSnapshot snapshot = HttpRequestSnapshot.Create(Settings(), "POST", "", null, null, Array.Empty<byte>(), null);

        Assert.False(snapshot.Headers.ContainsKey("Idempotency-Key"));
    }

    [Fact]
    public void Create_WithIdempotencyHeaderDisabled_DoesNotAddTheHeaderButKeepsTheKey()
    {
        EndpointSettings settings = Settings(idempotencyHeaderName: "");

        HttpRequestSnapshot snapshot = HttpRequestSnapshot.Create(settings, "POST", "", null, null, Array.Empty<byte>(), "key-1");

        Assert.Empty(snapshot.Headers);
        Assert.Equal("key-1", snapshot.IdempotencyKey);
    }

    [Fact]
    public void Create_UsesTheConfiguredIdempotencyHeaderName()
    {
        EndpointSettings settings = Settings(idempotencyHeaderName: "X-Request-Id");

        HttpRequestSnapshot snapshot = HttpRequestSnapshot.Create(settings, "POST", "", null, null, Array.Empty<byte>(), "key-1");

        Assert.Equal("key-1", snapshot.Headers["x-request-id"]);
    }

    [Fact]
    public void Create_HeadersAreCopiedAndCaseInsensitive()
    {
        var requestHeaders = new Dictionary<string, string> { ["X-A"] = "1" };

        HttpRequestSnapshot snapshot = HttpRequestSnapshot.Create(Settings(), "GET", "", requestHeaders, null, Array.Empty<byte>(), null);
        requestHeaders["X-A"] = "changed";
        requestHeaders["X-B"] = "added";

        Assert.Equal("1", snapshot.Headers["x-a"]);
        Assert.False(snapshot.Headers.ContainsKey("X-B"));
    }

    [Fact]
    public void Create_NullRequiredArguments_Throw()
    {
        EndpointSettings settings = Settings();

        Assert.Throws<ArgumentNullException>(() => HttpRequestSnapshot.Create(settings, null!, "", null, null, Array.Empty<byte>(), null));
        Assert.Throws<ArgumentNullException>(() => HttpRequestSnapshot.Create(settings, "GET", null!, null, null, Array.Empty<byte>(), null));
        Assert.Throws<ArgumentNullException>(() => HttpRequestSnapshot.Create(settings, "GET", "", null, null, null!, null));
    }

    private static EndpointSettings Settings(Dictionary<string, string>? defaultHeaders = null, string? idempotencyHeaderName = "Idempotency-Key")
        => EndpointConfigRules.CreateSettings(new TestEndpointConfig
        {
            DefaultHeaders = defaultHeaders ?? new Dictionary<string, string>(),
            IdempotencyHeaderName = idempotencyHeaderName,
        });
}
