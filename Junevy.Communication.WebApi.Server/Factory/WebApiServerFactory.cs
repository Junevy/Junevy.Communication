using Junevy.Communication.Core.Registry;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Junevy.Communication.WebApi.Server;

/// <summary>
/// HTTP 服务端工厂（P32-D14）：按名称创建、获取与移除服务器，行为与 WebApi 客户端工厂一致。
/// </summary>
/// <remarks>
/// 名称、别名与释放由 <see cref="NamedFactory{TItem}"/> 提供：同名获取返回同一实例，移除或释放时释放服务器。
/// 配置非法时由服务器的构造函数抛出 <see cref="ArgumentException"/>；工厂释放之后，除释放成员外的调用都抛出 <see cref="ObjectDisposedException"/>。
/// </remarks>
public sealed class WebApiServerFactory : NamedFactory<IWebApiServer>
{
    private readonly ILoggerFactory loggerFactory;

    /// <summary>创建服务器工厂。</summary>
    /// <param name="loggerFactory">日志工厂；为 null 时不输出日志。</param>
    public WebApiServerFactory(ILoggerFactory? loggerFactory = null)
        : base((loggerFactory ?? NullLoggerFactory.Instance).CreateLogger<WebApiServerFactory>())
    {
        this.loggerFactory = loggerFactory ?? NullLoggerFactory.Instance;
    }

    /// <summary>
    /// 按名称获取服务器；名称不存在时创建并注册。名称已存在时直接返回已有实例，<paramref name="config"/> 与 <paramref name="components"/> 不生效。
    /// </summary>
    /// <param name="name">服务器名称或别名；不能为 null 或空。</param>
    /// <param name="config">服务器配置。</param>
    /// <param name="components">代码级组件；可为 null。</param>
    /// <returns>已注册（或刚创建）的服务器，尚未启动。</returns>
    /// <exception cref="ArgumentNullException"><paramref name="config"/> 为 null。</exception>
    /// <exception cref="ArgumentException"><paramref name="name"/> 为空，或配置非法。</exception>
    /// <exception cref="ObjectDisposedException">工厂已释放。</exception>
    public IWebApiServer GetOrAdd(string name, WebApiServerConfig config, WebApiServerComponents? components = null)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(config);

        return GetOrAddCore(name, key => new WebApiServer(key, config, components, loggerFactory));
    }
}
