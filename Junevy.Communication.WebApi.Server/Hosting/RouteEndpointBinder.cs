using Junevy.Communication.WebApi.Server.Pipeline;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace Junevy.Communication.WebApi.Server.Hosting;

/// <summary>
/// 把登记的路由映射为 ASP.NET Core 的端点（内部，P32-D5）：模板与字面段优先由框架的路由匹配保证，处理器由管线执行。
/// </summary>
internal static class RouteEndpointBinder
{
    /// <summary>映射一条路由。</summary>
    /// <param name="endpoints">端点路由构建器（应用本身）。</param>
    /// <param name="definition">路由定义。</param>
    /// <param name="pipeline">请求管线。</param>
    /// <param name="serverName">服务器名称。</param>
    /// <param name="maxBodySize">正文上限（字节），用于异常翻译。</param>
    public static void Map(IEndpointRouteBuilder endpoints, RouteDefinition definition, RequestPipeline pipeline, string serverName, long maxBodySize)
    {
        endpoints.MapMethods(definition.Template, new[] { definition.Method }, context => RunAsync(context, definition, pipeline, serverName, maxBodySize));
    }

    private static async Task RunAsync(HttpContext context, RouteDefinition definition, RequestPipeline pipeline, string serverName, long maxBodySize)
    {
        long requestId = (long)context.Items[ServerKeys.RequestId]!;
        RawRequest raw = HttpRequestAdapter.Create(context, maxBodySize);
        ServerResult result = await pipeline.ExecuteAsync(raw, definition.Route, serverName, requestId, context.RequestAborted).ConfigureAwait(false);
        context.Items[ServerKeys.Result] = result;
    }
}
