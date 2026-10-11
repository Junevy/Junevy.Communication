using System.Net.Http;
using Junevy.Communication.Core.Results;
using Junevy.Communication.Core.Utils;

namespace Junevy.Communication.WebApi.Http;

/// <summary>
/// 读取 <see cref="HttpResponseMessage"/> 的响应体（内部）：先看 <c>Content-Length</c>，再在读取过程中计数，超过上限即以协议违规失败。
/// </summary>
internal static class HttpResponseReader
{
    private const int ReadBufferSize = 8192;
    private const string HeaderValueSeparator = ", ";

    /// <summary>
    /// 读完响应体并组装响应。超时 / 取消时释放响应以中断可能不响应令牌的读取（net472 的响应流），由调用方按作用域归类。
    /// </summary>
    /// <param name="response">已收到响应头的响应消息。</param>
    /// <param name="maxBodySize">响应体的最大字节数。</param>
    /// <param name="scope">本次尝试的超时作用域。</param>
    /// <returns>收到响应；或正文超限的失败结果。</returns>
    public static async Task<HttpExchangeOutcome> ReadAsync(HttpResponseMessage response, int maxBodySize, TimeoutScope scope)
    {
        int statusCode = (int)response.StatusCode;

        // 真实的 HttpClient 总会给响应一个内容对象，但注入的处理器（net472 上的 HttpResponseMessage 默认没有内容）可以不给，按空正文处理。
        HttpContent? content = response.Content;
        long? declared = content?.Headers.ContentLength;
        if (declared > maxBodySize)
            return TooLarge(maxBodySize, statusCode);

        byte[]? body = content == null ? Array.Empty<byte>() : await ReadBodyAsync(response, content, maxBodySize, scope.Token).ConfigureAwait(false);
        if (body == null)
            return TooLarge(maxBodySize, statusCode);

        string? contentType = content?.Headers.ContentType?.ToString();
        return HttpExchangeOutcome.Received(new HttpExchangeResponse(statusCode, response.ReasonPhrase, FlattenHeaders(response, content), body, contentType));
    }

    private static async Task<byte[]?> ReadBodyAsync(HttpResponseMessage response, HttpContent content, int maxBodySize, CancellationToken token)
    {
        using Stream stream = await content.ReadAsStreamAsync().ConfigureAwait(false);
        using CancellationTokenRegistration abort = token.Register(response.Dispose);
        using var buffer = new MemoryStream();
        var chunk = new byte[ReadBufferSize];
        int read;
        while ((read = await stream.ReadAsync(chunk, 0, chunk.Length, token).ConfigureAwait(false)) > 0)
        {
            if (buffer.Length + read > maxBodySize)
                return null;

            buffer.Write(chunk, 0, read);
        }

        return buffer.ToArray();
    }

    private static IReadOnlyDictionary<string, string> FlattenHeaders(HttpResponseMessage response, HttpContent? content)
    {
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        IEnumerable<KeyValuePair<string, IEnumerable<string>>> all = content == null ? response.Headers : response.Headers.Concat(content.Headers);
        foreach (KeyValuePair<string, IEnumerable<string>> header in all)
            headers[header.Key] = string.Join(HeaderValueSeparator, header.Value);

        return headers;
    }

    private static HttpExchangeOutcome TooLarge(int maxBodySize, int statusCode)
        => HttpExchangeOutcome.Failed(CommErrorKind.ProtocolViolation, $"The response body exceeds the limit of {maxBodySize} bytes.", null, statusCode);
}
