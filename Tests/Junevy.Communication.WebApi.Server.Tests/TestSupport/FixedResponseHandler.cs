using System.Net;
using System.Net.Http.Headers;
using Junevy.Communication.WebApi.Server.Formatting;

namespace Junevy.Communication.WebApi.Server.Tests.TestSupport;

/// <summary>返回固定响应的 HTTP 处理器：把服务端写出的响应原样交给 3.1 客户端解析（往返用例）。</summary>
internal sealed class FixedResponseHandler : HttpMessageHandler
{
    private readonly ServerResponse response;

    public FixedResponseHandler(ServerResponse response)
    {
        this.response = response;
    }

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var message = new HttpResponseMessage((HttpStatusCode)response.StatusCode)
        {
            Content = new ByteArrayContent(response.Body ?? Array.Empty<byte>()),
        };
        if (response.ContentType != null)
            message.Content.Headers.ContentType = MediaTypeHeaderValue.Parse(response.ContentType);
        foreach ((string name, string value) in response.Headers)
            message.Headers.TryAddWithoutValidation(name, value);

        return Task.FromResult(message);
    }
}
