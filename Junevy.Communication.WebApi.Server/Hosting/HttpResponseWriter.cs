using Junevy.Communication.WebApi.Server.Formatting;
using Microsoft.AspNetCore.Http;

namespace Junevy.Communication.WebApi.Server.Hosting;

/// <summary>
/// 把格式化好的响应写到 <c>HttpResponse</c>（内部）。状态码、额外头、内容类型与正文都在这里写入；没有正文时不写 <c>Content-Type</c>。
/// </summary>
internal static class HttpResponseWriter
{
    /// <summary>写出响应。</summary>
    /// <param name="response">目标响应。</param>
    /// <param name="payload">已格式化的响应。</param>
    /// <param name="cancellationToken">请求的取消令牌（对端断开时触发）。</param>
    /// <returns>写出完成的任务；对端已断开时抛出异常。</returns>
    public static async Task WriteAsync(HttpResponse response, ServerResponse payload, CancellationToken cancellationToken)
    {
        response.StatusCode = payload.StatusCode;
        foreach (KeyValuePair<string, string> header in payload.Headers)
            response.Headers[header.Key] = header.Value;

        if (payload.Body == null)
            return;

        response.ContentType = payload.ContentType;
        response.ContentLength = payload.Body.Length;
        await response.Body.WriteAsync(payload.Body, cancellationToken).ConfigureAwait(false);
    }
}
