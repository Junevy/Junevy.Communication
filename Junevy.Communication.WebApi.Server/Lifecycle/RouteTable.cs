using Junevy.Communication.WebApi.Server.Pipeline;

namespace Junevy.Communication.WebApi.Server.Lifecycle;

/// <summary>
/// 已登记的路由（内部）：同一方法下模板不能重复。启动时取快照，之后的注册不影响正在运行的应用。
/// </summary>
internal sealed class RouteTable
{
    private readonly List<RouteDefinition> routes = new List<RouteDefinition>();
    private readonly object gate = new object();

    /// <summary>已登记的路由数。</summary>
    public int Count
    {
        get
        {
            lock (gate)
                return routes.Count;
        }
    }

    /// <summary>登记一条路由。</summary>
    /// <param name="route">路由定义。</param>
    /// <exception cref="ArgumentException">同一方法下模板已经登记。</exception>
    public void Add(RouteDefinition route)
    {
        lock (gate)
        {
            if (routes.Any(existing => existing.Method == route.Method && existing.Template == route.Template))
                throw new ArgumentException($"A {route.Method} route for '{route.Template}' is already registered.", nameof(route));

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
