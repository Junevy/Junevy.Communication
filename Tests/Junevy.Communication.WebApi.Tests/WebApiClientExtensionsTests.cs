using System.Net.Http;
using Junevy.Communication.WebApi.Rest;

namespace Junevy.Communication.WebApi.Tests;

/// <summary>
/// <see cref="WebApiClientExtensions"/> 的便捷方法：每个方法只是构造一个 <see cref="WebApiRequest"/> 并交给 <c>SendAsync</c>。
/// </summary>
public sealed class WebApiClientExtensionsTests
{
    [Fact]
    public async Task GetAsync_BuildsAGetWithPathAndQuery()
    {
        var client = new RecordingClient();
        var query = new Dictionary<string, string?> { ["id"] = "7" };
        using var source = new CancellationTokenSource();

        await client.GetAsync<string>("orders", query, source.Token);

        WebApiRequest request = client.Last!;
        Assert.Equal(HttpMethod.Get, request.Method);
        Assert.Equal("orders", request.Path);
        Assert.Same(query, request.Query);
        Assert.Null(request.Body);
        Assert.Equal(source.Token, client.LastToken);
    }

    [Fact]
    public async Task PostAsync_BuildsAPostWithBodyAndIdempotencyKey()
    {
        var client = new RecordingClient();
        var body = new { A = 1 };

        await client.PostAsync<string>("orders", body, "key-1");

        Assert.Equal(HttpMethod.Post, client.Last!.Method);
        Assert.Same(body, client.Last.Body);
        Assert.Equal("key-1", client.Last.IdempotencyKey);
    }

    [Fact]
    public async Task PutAsync_BuildsAPutWithBodyAndIdempotencyKey()
    {
        var client = new RecordingClient();
        var body = new { A = 1 };

        await client.PutAsync<string>("orders/7", body, "key-2");

        Assert.Equal(HttpMethod.Put, client.Last!.Method);
        Assert.Equal("orders/7", client.Last.Path);
        Assert.Same(body, client.Last.Body);
        Assert.Equal("key-2", client.Last.IdempotencyKey);
    }

    [Fact]
    public async Task DeleteAsync_BuildsADelete()
    {
        var client = new RecordingClient();

        await client.DeleteAsync<string>("orders/7");

        Assert.Equal(HttpMethod.Delete, client.Last!.Method);
        Assert.Equal("orders/7", client.Last.Path);
    }

    [Fact]
    public async Task Extensions_ReturnWhatSendAsyncReturns()
    {
        var client = new RecordingClient();

        WebApiResult<string> result = await client.GetAsync<string>("x");

        Assert.Same(client.Result, result);
    }

    [Fact]
    public void Extensions_NullClient_Throw()
    {
        IWebApiClient client = null!;

        Assert.Throws<ArgumentNullException>(() => { _ = client.GetAsync<string>("x"); });
        Assert.Throws<ArgumentNullException>(() => { _ = client.PostAsync<string>("x", null); });
        Assert.Throws<ArgumentNullException>(() => { _ = client.PutAsync<string>("x", null); });
        Assert.Throws<ArgumentNullException>(() => { _ = client.DeleteAsync<string>("x"); });
    }

    private sealed class RecordingClient : IWebApiClient
    {
        public WebApiRequest? Last { get; private set; }

        public CancellationToken LastToken { get; private set; }

        public WebApiResult<string> Result { get; } = WebApiResult<string>.Success("ok", 200);

        public WebApiClientConfig Config => throw new NotSupportedException();

        public string Name => "recording";

        public WebApiAvailability Availability => WebApiAvailability.Unknown;

        public event EventHandler<WebApiAvailabilityChangedEventArgs>? AvailabilityChanged { add { } remove { } }

        public event EventHandler<WebApiSendingEventArgs>? RequestSending { add { } remove { } }

        public event EventHandler<WebApiResponseEventArgs>? ResponseReceived { add { } remove { } }

        public event EventHandler<WebApiCompletedEventArgs>? RequestCompleted { add { } remove { } }

        public Task<WebApiResult<TResponse>> SendAsync<TResponse>(WebApiRequest request, CancellationToken cancellationToken = default)
        {
            Last = request;
            LastToken = cancellationToken;
            return Task.FromResult((WebApiResult<TResponse>)(object)Result);
        }

        public void Dispose()
        {
        }

        public ValueTask DisposeAsync() => default;
    }
}
