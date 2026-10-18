namespace Junevy.Communication.WebApi.Server.Pipeline;

/// <summary>
/// 已注册路由的处理器（内部）：类型擦除后的统一入口。管线只知道请求类型（有正文的路由）与"调用并得到已格式化的结果"。
/// </summary>
internal interface IRoute
{
    /// <summary>请求正文的类型；没有正文的路由（GET、DELETE）为 null。</summary>
    Type? RequestType { get; }

    /// <summary>调用处理器，并把返回值格式化为线上的响应。处理器抛出的异常原样向上传播。</summary>
    /// <param name="request">已解码的请求对象；没有正文的路由为 null。</param>
    /// <param name="context">请求上下文。</param>
    /// <param name="cancellationToken">取消令牌（超时或对端断开时触发）。</param>
    /// <returns>处理结果。</returns>
    Task<ServerResult> InvokeAsync(object? request, WebApiServerContext context, CancellationToken cancellationToken);
}
