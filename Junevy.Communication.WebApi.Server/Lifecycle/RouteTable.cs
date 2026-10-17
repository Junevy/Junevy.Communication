using Junevy.Communication.WebApi.Server.Pipeline;

namespace Junevy.Communication.WebApi.Server.Lifecycle;

/// <summary>
/// 已登记的路由（内部）：同一方法下模板不能重复。启动时取快照，之后的注册不影响正在运行的应用。
/// 重复按 ASP.NET Core 的匹配规则判断：忽略首尾的 <c>/</c> 与大小写，因为 <c>/orders</c>、<c>Orders/</c> 匹配同样的请求，
/// 同时登记只会在每次请求时引发歧义（500）。
/// </summary>
internal sealed class RouteTable
{
    private readonly List<RouteDefinition> routes = new List<RouteDefinition>();
    private readonly HashSet<string> keys = new HashSet<string>(StringComparer.Ordinal);
    private readonly object gate = new object();

    /// <summary>登记一条路由。</summary>
    /// <param name="route">路由定义。</param>
    /// <exception cref="ArgumentException">同一方法下已经登记了等价的模板。</exception>
    public void Add(RouteDefinition route)
    {
        string key = route.Method + " " + route.Template.Trim('/').ToUpperInvariant();
        lock (gate)
        {
            if (!keys.Add(key))
                throw new ArgumentException($"A {route.Method} route equivalent to '{route.Template}' is already registered.", nameof(route));

            routes.Add(route);
        }
    }

    /// <summary>路由的快照。</summary>
    /// <returns>路由列表的副本。</returns>
    public IReadOnlyList<RouteDefinition> Snapshot()
    {
        lock (gate)
            return routes.ToArray();
    }
}
