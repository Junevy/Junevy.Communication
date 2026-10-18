using Junevy.Communication.WebApi.Server.Config;
using Junevy.Communication.WebApi.Server.Formatting;
using Junevy.Communication.WebApi.Server.Pipeline;

namespace Junevy.Communication.WebApi.Server.Tests.TestSupport;

/// <summary>测试用的路由构造：与 <c>WebApiServer.Map*</c> 相同的类型化适配，但直接产生内部的路由定义。</summary>
internal static class TestRoutes
{
    /// <summary>GET 路由（无正文）。</summary>
    public static RouteDefinition Get<TResponse>(
        WebApiServerSettings settings,
        string template,
        Func<WebApiServerContext, CancellationToken, Task<WebApiServerResponse<TResponse>>> handler)
        => new RouteDefinition("GET", template, new TypedRoute<TResponse>(null, (_, context, token) => handler(context, token), new ResponseFormatter(settings)));

    /// <summary>DELETE 路由（无正文）。</summary>
    public static RouteDefinition Delete<TResponse>(
        WebApiServerSettings settings,
        string template,
        Func<WebApiServerContext, CancellationToken, Task<WebApiServerResponse<TResponse>>> handler)
        => new RouteDefinition("DELETE", template, new TypedRoute<TResponse>(null, (_, context, token) => handler(context, token), new ResponseFormatter(settings)));

    /// <summary>POST 路由（JSON 正文）。</summary>
    public static RouteDefinition Post<TRequest, TResponse>(
        WebApiServerSettings settings,
        string template,
        Func<TRequest, WebApiServerContext, CancellationToken, Task<WebApiServerResponse<TResponse>>> handler)
        => new RouteDefinition("POST", template, Body(settings, handler));

    /// <summary>PUT 路由（JSON 正文）。</summary>
    public static RouteDefinition Put<TRequest, TResponse>(
        WebApiServerSettings settings,
        string template,
        Func<TRequest, WebApiServerContext, CancellationToken, Task<WebApiServerResponse<TResponse>>> handler)
        => new RouteDefinition("PUT", template, Body(settings, handler));

    private static IRoute Body<TRequest, TResponse>(
        WebApiServerSettings settings,
        Func<TRequest, WebApiServerContext, CancellationToken, Task<WebApiServerResponse<TResponse>>> handler)
        => new TypedRoute<TResponse>(
            typeof(TRequest),
            (request, context, token) => handler((TRequest)request!, context, token),
            new ResponseFormatter(settings));
}
