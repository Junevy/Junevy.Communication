using System.Net.Http;
using System.Text;
using Junevy.Communication.WebApi.Http;
using Junevy.Communication.WebApi.Rest;
using Junevy.Communication.WebApi.Serialization;

namespace Junevy.Communication.WebApi.Tests;

/// <summary>
/// <see cref="RestRequestBuilder"/> 的测试（计划 Task 8 第 1 条）：请求校验、路径与查询拼接、头合并、正文序列化。
/// </summary>
public sealed class RestRequestBuilderTests
{
    public static IEnumerable<object[]> InvalidRequests()
    {
        yield return new object[] { "request is null", (Func<WebApiRequest?>)(() => null) };
        yield return new object[] { "method is null", (Func<WebApiRequest?>)(() => new WebApiRequest { Method = null! }) };
        yield return new object[] { "path is null", (Func<WebApiRequest?>)(() => new WebApiRequest { Path = null! }) };
        yield return new object[] { "absolute path", (Func<WebApiRequest?>)(() => new WebApiRequest { Path = "http://evil.local/steal" }) };
        yield return new object[] { "protocol-relative path", (Func<WebApiRequest?>)(() => new WebApiRequest { Path = "//evil.local/steal" }) };
        yield return new object[] { "unc path", (Func<WebApiRequest?>)(() => new WebApiRequest { Path = "\\\\evil.local\\share" }) };
        yield return new object[] { "scheme in first segment", (Func<WebApiRequest?>)(() => new WebApiRequest { Path = "orders:1" }) };
        yield return new object[] { "byte body without content type", (Func<WebApiRequest?>)(() => new WebApiRequest { Method = HttpMethod.Post, Body = new byte[] { 1 } }) };
        yield return new object[] { "string body without content type", (Func<WebApiRequest?>)(() => new WebApiRequest { Method = HttpMethod.Post, Body = "text" }) };
        yield return new object[] { "zero timeout", (Func<WebApiRequest?>)(() => new WebApiRequest { Timeout = 0 }) };
        yield return new object[] { "negative timeout", (Func<WebApiRequest?>)(() => new WebApiRequest { Timeout = -5 }) };
        yield return new object[] { "empty query name", (Func<WebApiRequest?>)(() => new WebApiRequest { Query = new Dictionary<string, string?> { [""] = "x" } }) };
    }

    [Theory]
    [MemberData(nameof(InvalidRequests))]
    public void TryBuild_InvalidRequest_FailsWithAnEnglishMessage(string scenario, Func<WebApiRequest?> create)
    {
        Assert.NotNull(scenario);

        bool built = Builder().TryBuild(create(), out HttpRequestSnapshot? snapshot, out string? error);

        Assert.False(built);
        Assert.Null(snapshot);
        Assert.False(string.IsNullOrWhiteSpace(error));
    }

    [Theory]
    [InlineData("orders", "orders")]
    [InlineData("/orders", "orders")]
    [InlineData("orders/7", "orders/7")]
    [InlineData("", "")]
    [InlineData("/", "")]
    [InlineData("orders?id=7", "orders?id=7")]
    [InlineData("orders/a:b", "orders/a:b")]
    public void TryBuild_PathIsRelativeToTheBaseAddress(string path, string expectedRelative)
    {
        Assert.True(Builder().TryBuild(new WebApiRequest { Path = path }, out HttpRequestSnapshot? snapshot, out _));

        Assert.Equal(expectedRelative, snapshot!.RelativeUri);
    }

    [Fact]
    public void TryBuild_QueryValuesAreEscapedAndNullValuesAreSkipped()
    {
        var request = new WebApiRequest
        {
            Path = "search",
            Query = new Dictionary<string, string?>
            {
                ["q"] = "a b",
                ["name"] = "工单",
                ["expr"] = "x&y=z",
                ["skip"] = null,
                ["k e y"] = "v",
            },
        };

        Assert.True(Builder().TryBuild(request, out HttpRequestSnapshot? snapshot, out _));

        Assert.Equal("search?q=a%20b&name=%E5%B7%A5%E5%8D%95&expr=x%26y%3Dz&k%20e%20y=v", snapshot!.RelativeUri);
    }

    [Fact]
    public void TryBuild_QueryIsAppendedWithAmpersandWhenThePathAlreadyHasOne()
    {
        var request = new WebApiRequest { Path = "search?a=1", Query = new Dictionary<string, string?> { ["b"] = "2" } };

        Assert.True(Builder().TryBuild(request, out HttpRequestSnapshot? snapshot, out _));

        Assert.Equal("search?a=1&b=2", snapshot!.RelativeUri);
    }

    [Fact]
    public void TryBuild_AnAllNullQuery_AddsNoQuestionMark()
    {
        var request = new WebApiRequest { Path = "search", Query = new Dictionary<string, string?> { ["a"] = null } };

        Assert.True(Builder().TryBuild(request, out HttpRequestSnapshot? snapshot, out _));

        Assert.Equal("search", snapshot!.RelativeUri);
    }

    [Fact]
    public void TryBuild_HeadersMergeDefaultsThenRequestThenTheIdempotencyKey()
    {
        EndpointSettings endpoint = Endpoint(new Dictionary<string, string> { ["X-Default"] = "d", ["X-Shared"] = "from-default" });
        var request = new WebApiRequest
        {
            Method = HttpMethod.Post,
            Headers = new Dictionary<string, string> { ["x-shared"] = "from-request", ["X-Own"] = "o" },
            IdempotencyKey = "key-9",
        };

        Assert.True(new RestRequestBuilder(endpoint, new SystemTextJsonWebApiSerializer()).TryBuild(request, out HttpRequestSnapshot? snapshot, out _));

        Assert.Equal("d", snapshot!.Headers["X-Default"]);
        Assert.Equal("from-request", snapshot.Headers["X-Shared"]);
        Assert.Equal("o", snapshot.Headers["X-Own"]);
        Assert.Equal("key-9", snapshot.Headers["Idempotency-Key"]);
        Assert.Equal("key-9", snapshot.IdempotencyKey);
    }

    [Fact]
    public void TryBuild_CarriesTheMethod()
    {
        Assert.True(Builder().TryBuild(new WebApiRequest { Method = HttpMethod.Delete }, out HttpRequestSnapshot? snapshot, out _));

        Assert.Equal("DELETE", snapshot!.Method);
    }

    [Fact]
    public void TryBuild_ByteAndStringBodies_AreSentVerbatimWithTheGivenContentType()
    {
        Assert.True(Builder().TryBuild(new WebApiRequest { Method = HttpMethod.Post, Body = new byte[] { 1, 2, 3 }, ContentType = "application/octet-stream" },
                                       out HttpRequestSnapshot? bytes, out _));
        Assert.True(Builder().TryBuild(new WebApiRequest { Method = HttpMethod.Post, Body = "a=1&b=工单", ContentType = "text/plain; charset=utf-8" },
                                       out HttpRequestSnapshot? text, out _));

        Assert.Equal(new byte[] { 1, 2, 3 }, bytes!.Body);
        Assert.Equal("application/octet-stream", bytes.ContentType);
        Assert.Equal(Encoding.UTF8.GetBytes("a=1&b=工单"), text!.Body);
        Assert.Equal("text/plain; charset=utf-8", text.ContentType);
    }

    [Fact]
    public void TryBuild_ObjectBody_IsSerializedWithTheSerializerAndItsContentType()
    {
        var request = new WebApiRequest { Method = HttpMethod.Post, Body = new { OrderNo = "工单-1", Quantity = 2 } };

        Assert.True(Builder().TryBuild(request, out HttpRequestSnapshot? snapshot, out _));

        string json = Encoding.UTF8.GetString(snapshot!.Body);
        Assert.Equal("{\"orderNo\":\"工单-1\",\"quantity\":2}", json);
        Assert.Equal("application/json; charset=utf-8", snapshot.ContentType);
    }

    [Fact]
    public void TryBuild_ObjectBody_AllowsAnExplicitContentTypeToOverrideTheSerializersOne()
    {
        var request = new WebApiRequest { Method = HttpMethod.Post, Body = new { A = 1 }, ContentType = "application/vnd.mes+json" };

        Assert.True(Builder().TryBuild(request, out HttpRequestSnapshot? snapshot, out _));

        Assert.Equal("application/vnd.mes+json", snapshot!.ContentType);
    }

    [Fact]
    public void TryBuild_NoBody_HasAnEmptyBodyAndTheRequestedContentType()
    {
        Assert.True(Builder().TryBuild(new WebApiRequest { Method = HttpMethod.Get }, out HttpRequestSnapshot? snapshot, out _));

        Assert.Empty(snapshot!.Body);
        Assert.Null(snapshot.ContentType);
    }

    [Fact]
    public void TryBuild_ABodyTheSerializerCannotHandle_FailsAsInvalidRequestText()
    {
        var builder = new RestRequestBuilder(Endpoint(), new ThrowingSerializer());

        bool built = builder.TryBuild(new WebApiRequest { Method = HttpMethod.Post, Body = new { A = 1 } }, out HttpRequestSnapshot? snapshot, out string? error);

        Assert.False(built);
        Assert.Null(snapshot);
        Assert.Contains("serialized", error);
        Assert.Contains("cannot serialize", error);
    }

    [Fact]
    public void TryBuild_UsesTheSuppliedSerializer()
    {
        var builder = new RestRequestBuilder(Endpoint(), new ThrowingSerializer { Fail = false });

        Assert.True(builder.TryBuild(new WebApiRequest { Method = HttpMethod.Post, Body = new { A = 1 } }, out HttpRequestSnapshot? snapshot, out _));

        Assert.Equal("serialized-by-test", Encoding.UTF8.GetString(snapshot!.Body));
        Assert.Equal("application/x-test", snapshot.ContentType);
    }

    private static RestRequestBuilder Builder() => new RestRequestBuilder(Endpoint(), new SystemTextJsonWebApiSerializer());

    private static EndpointSettings Endpoint(Dictionary<string, string>? defaultHeaders = null)
        => EndpointConfigRules.CreateSettings(new TestEndpointConfig { DefaultHeaders = defaultHeaders ?? new Dictionary<string, string>() });

    private sealed class ThrowingSerializer : IWebApiSerializer
    {
        public bool Fail { get; set; } = true;

        public string ContentType => "application/x-test";

        public byte[] Serialize(object value, Type type)
            => Fail ? throw new NotSupportedException("cannot serialize this type") : Encoding.UTF8.GetBytes("serialized-by-test");

        public object? Deserialize(byte[] data, Type type) => throw new NotSupportedException();
    }
}
