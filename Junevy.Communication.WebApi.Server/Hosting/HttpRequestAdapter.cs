using System.Net;
using Junevy.Communication.WebApi.Server.Pipeline;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Primitives;

namespace Junevy.Communication.WebApi.Server.Hosting;

/// <summary>
/// 把 <c>HttpContext</c> 翻译为与 HTTP 实现无关的 <see cref="RawRequest"/>（内部）。
/// 正文的读取在这里完成，并把 Kestrel 的 413 异常翻译为本库的 <see cref="RequestBodyTooLargeException"/>。
/// </summary>
internal static class HttpRequestAdapter
{
    /// <summary>创建请求快照。</summary>
    /// <param name="context">当前的 HTTP 上下文。</param>
    /// <param name="maxBodySize">配置的正文上限（用于异常消息）。</param>
    /// <returns>请求快照。</returns>
    public static RawRequest Create(HttpContext context, long maxBodySize)
    {
        HttpRequest request = context.Request;
        return new RawRequest(
            request.Method.ToUpperInvariant(),
            request.Path.Value ?? "/",
            FirstValues(request.Query),
            JoinedHeaders(request.Headers),
            request.ContentType,
            RemoteEndPointOf(context),
            (context.GetEndpoint() as RouteEndpoint)?.RoutePattern.RawText,
            RouteValuesOf(request),
            token => ReadBodyAsync(request, maxBodySize, token));
    }

    private static async Task<byte[]> ReadBodyAsync(HttpRequest request, long maxBodySize, CancellationToken cancellationToken)
    {
        using var buffer = new MemoryStream();
        try
        {
            await request.Body.CopyToAsync(buffer, cancellationToken).ConfigureAwait(false);
        }
        catch (Microsoft.AspNetCore.Http.BadHttpRequestException ex)
        {
            // Kestrel 在读取时抛出 BadHttpRequestException（.NET 8 中为 Http 基类的派生类，本身是 IOException）：
            // 413 翻译为本库的类型；其余的畸形正文翻译为非 IO 异常，避免被管线当成对端断开。
            if (ex.StatusCode == StatusCodes.Status413RequestEntityTooLarge)
                throw new RequestBodyTooLargeException(maxBodySize, ex);

            throw new RequestBodyMalformedException(ex);
        }

        return buffer.ToArray();
    }

    // 查询参数的名称区分大小写；同名参数取第一个（P32 Task 3）。
    private static IReadOnlyDictionary<string, string> FirstValues(IQueryCollection query)
    {
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (KeyValuePair<string, StringValues> pair in query)
            values[pair.Key] = pair.Value.FirstOrDefault() ?? string.Empty;

        return values;
    }

    // 请求头不区分大小写；同名多值以 ", " 合并（RFC 9110 第 5.3 节）。
    private static IReadOnlyDictionary<string, string> JoinedHeaders(IHeaderDictionary headers)
    {
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (KeyValuePair<string, StringValues> pair in headers)
            values[pair.Key] = pair.Value.ToString();

        return values;
    }

    private static IReadOnlyDictionary<string, string> RouteValuesOf(HttpRequest request)
    {
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (KeyValuePair<string, object?> pair in request.RouteValues)
        {
            if (pair.Value != null)
                values[pair.Key] = pair.Value.ToString() ?? string.Empty;
        }

        return values;
    }

    /// <summary>对端地址；取不到时为 null。</summary>
    /// <param name="context">当前的 HTTP 上下文。</param>
    /// <returns>对端地址。</returns>
    public static IPEndPoint? RemoteEndPointOf(HttpContext context)
        => context.Connection.RemoteIpAddress is { } address
            ? new IPEndPoint(address, context.Connection.RemotePort)
            : null;
}
