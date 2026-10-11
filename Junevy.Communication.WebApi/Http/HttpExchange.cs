using System.Net.Http;
using Junevy.Communication.Core.Results;
using Junevy.Communication.Core.Utils;

namespace Junevy.Communication.WebApi.Http;

/// <summary>
/// 一次尝试的 HTTP 收发（内部）：把请求上下文发出去，读完响应，返回"收到响应"或"失败"。
/// 只负责传输；超时作用域由调用方（尝试循环）创建并覆盖拦截器，重试与事件也不在这里。
/// </summary>
internal sealed class HttpExchange
{
    private readonly HttpClient client;
    private readonly int maxResponseBodySize;

    /// <summary>创建交换器。</summary>
    /// <param name="client">端点的长期存活的 HttpClient（<c>Timeout</c> 应为无限，超时由作用域控制）。</param>
    /// <param name="maxResponseBodySize">响应体的最大字节数。</param>
    public HttpExchange(HttpClient client, int maxResponseBodySize)
    {
        this.client = client;
        this.maxResponseBodySize = maxResponseBodySize;
    }

    /// <summary>
    /// 发送请求并读完响应。运行期失败都转换为结果，不抛出异常（取消与超时也归类，见 <see cref="HttpStatusClassifier.FromException"/>）。
    /// </summary>
    /// <param name="context">拦截器处理之后的请求上下文。</param>
    /// <param name="scope">本次尝试的超时作用域。</param>
    /// <returns>交换结果。</returns>
    public async Task<HttpExchangeOutcome> SendAsync(WebApiSendingContext context, TimeoutScope scope)
    {
        if (!HttpRequestBuilder.TryBuild(context, out HttpRequestMessage? request, out string? error))
            return HttpExchangeOutcome.Failed(CommErrorKind.InvalidRequest, error!);

        HttpResponseMessage? response = null;
        try
        {
            response = await client.SendAsync(request!, HttpCompletionOption.ResponseHeadersRead, scope.Token).ConfigureAwait(false);
            return await HttpResponseReader.ReadAsync(response, maxResponseBodySize, scope).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // 边界：HTTP 栈在网络、超时、取消时会抛出各种异常类型，统一归类为结果。
            (CommErrorKind kind, string message) = HttpStatusClassifier.FromException(ex, scope);
            return HttpExchangeOutcome.Failed(kind, message, ex);
        }
        finally
        {
            response?.Dispose();
            request!.Dispose();
        }
    }
}
