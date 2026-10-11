using System.Net.Http;

namespace Junevy.Communication.WebApi.Http;

/// <summary>
/// 把一次尝试的请求（拦截器处理之后的方法、URI、头、正文）转换为 <see cref="HttpRequestMessage"/>（内部）。
/// </summary>
internal static class HttpRequestBuilder
{
    private const string ContentTypeHeader = "Content-Type";

    /// <summary>
    /// 转换请求。请求头先加到请求头集合；失败的（例如 <c>Content-Language</c> 这类内容头）再加到内容头集合；两处都失败说明头名或头值非法。
    /// 显式关闭 <c>Expect: 100-continue</c>（net472 默认带它，每个 POST 多等一个往返）。
    /// </summary>
    /// <param name="context">拦截器处理之后的请求上下文。</param>
    /// <param name="request">成功时的请求消息，调用方负责释放。</param>
    /// <param name="error">失败时的英文信息，指出非法的头。</param>
    /// <returns>是否成功。</returns>
    public static bool TryBuild(WebApiSendingContext context, out HttpRequestMessage? request, out string? error)
    {
        var message = new HttpRequestMessage(new HttpMethod(context.Method), context.RequestUri);
        message.Headers.ExpectContinue = false;

        // 没有正文也没有内容类型时不创建内容对象，GET 才不会带一个空内容。
        HttpContent? content = context.Body.Length > 0 || context.ContentType != null ? new ByteArrayContent(context.Body) : null;
        foreach (KeyValuePair<string, string> header in context.Headers)
        {
            if (!message.Headers.TryAddWithoutValidation(header.Key, header.Value)
                && (content == null || !content.Headers.TryAddWithoutValidation(header.Key, header.Value)))
            {
                message.Dispose();
                content?.Dispose();
                request = null;
                error = $"The header '{header.Key}' is not valid for this request.";
                return false;
            }
        }

        if (content != null && context.ContentType != null)
        {
            content.Headers.Remove(ContentTypeHeader);
            content.Headers.TryAddWithoutValidation(ContentTypeHeader, context.ContentType);
        }

        message.Content = content;
        request = message;
        error = null;
        return true;
    }
}
