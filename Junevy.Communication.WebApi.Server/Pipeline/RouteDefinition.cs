namespace Junevy.Communication.WebApi.Server.Pipeline;

/// <summary>
/// 一条已登记的路由（内部）：HTTP 方法、ASP.NET Core 路由模板，以及处理器。
/// </summary>
/// <param name="Method">HTTP 方法，大写，例如 <c>GET</c>。</param>
/// <param name="Template">路由模板，例如 <c>/orders/{id:int}</c>。</param>
/// <param name="Route">处理器。</param>
internal sealed record RouteDefinition(string Method, string Template, IRoute Route);
