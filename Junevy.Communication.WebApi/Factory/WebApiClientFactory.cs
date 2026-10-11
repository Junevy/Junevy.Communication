using Junevy.Communication.Core.Registry;
using Junevy.Communication.WebApi.Rest;
using Junevy.Communication.WebApi.Soap;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Junevy.Communication.WebApi.Factory;

/// <summary>
/// WebApi 客户端工厂：按名称创建、获取与移除 REST 与 SOAP 客户端，同一个名称空间里两种客户端并存。
/// 行为与 <c>ChannelFactory</c>、<c>PlcClientFactory</c> 一致。
/// </summary>
/// <remarks>
/// 名称、别名与释放由 <see cref="NamedFactory{TItem}"/> 提供（底层为 <see cref="NamedRegistry{T}"/>）：同名获取返回同一实例，别名与其目标共享实例，
/// 移除或释放时释放客户端（健康检查随之停止，注入的 HTTP 处理器不会被释放）。配置非法时由客户端的构造函数抛出 <see cref="ArgumentException"/>。
/// 工厂释放之后，除 <see cref="NamedFactory{TItem}.Dispose()"/> 与 <see cref="NamedFactory{TItem}.DisposeAsync"/> 外的成员都抛出 <see cref="ObjectDisposedException"/>。
/// </remarks>
public sealed class WebApiClientFactory : NamedFactory<IWebApiEndpoint>
{
    private readonly ILoggerFactory loggerFactory;

    /// <summary>创建客户端工厂。</summary>
    /// <param name="loggerFactory">日志工厂；为 null 时不输出日志。</param>
    public WebApiClientFactory(ILoggerFactory? loggerFactory = null)
        : base((loggerFactory ?? NullLoggerFactory.Instance).CreateLogger<WebApiClientFactory>())
    {
        this.loggerFactory = loggerFactory ?? NullLoggerFactory.Instance;
    }

    /// <summary>
    /// 按名称获取 REST 客户端；名称不存在时创建并注册。名称已存在时直接返回已有实例，<paramref name="config"/> 与 <paramref name="components"/> 不生效。
    /// </summary>
    /// <param name="name">客户端名称或别名；不能为 null 或空。</param>
    /// <param name="config">REST 配置。</param>
    /// <param name="components">代码级组件；可为 null。</param>
    /// <returns>已注册（或刚创建）的 REST 客户端。</returns>
    /// <exception cref="ArgumentNullException"><paramref name="config"/> 为 null。</exception>
    /// <exception cref="ArgumentException"><paramref name="name"/> 为 null 或空，或配置非法。</exception>
    /// <exception cref="InvalidOperationException">该名称已注册为 SOAP 客户端。</exception>
    /// <exception cref="ObjectDisposedException">工厂已释放。</exception>
    public IWebApiClient GetOrAdd(string name, WebApiClientConfig config, WebApiComponents? components = null)
    {
        ThrowIfDisposed();
        if (config == null)
            throw new ArgumentNullException(nameof(config));

        return GetOrAddTyped<IWebApiClient>(name, key => new WebApiClient(key, config, loggerFactory.CreateLogger<WebApiClient>(), components));
    }

    /// <summary>
    /// 按名称获取 SOAP 客户端；名称不存在时创建并注册。名称已存在时直接返回已有实例，<paramref name="config"/> 与 <paramref name="components"/> 不生效。
    /// </summary>
    /// <param name="name">客户端名称或别名；不能为 null 或空。</param>
    /// <param name="config">SOAP 配置。</param>
    /// <param name="components">代码级组件；可为 null。</param>
    /// <returns>已注册（或刚创建）的 SOAP 客户端。</returns>
    /// <exception cref="ArgumentNullException"><paramref name="config"/> 为 null。</exception>
    /// <exception cref="ArgumentException"><paramref name="name"/> 为 null 或空，或配置非法。</exception>
    /// <exception cref="InvalidOperationException">该名称已注册为 REST 客户端。</exception>
    /// <exception cref="ObjectDisposedException">工厂已释放。</exception>
    public ISoapClient GetOrAdd(string name, SoapClientConfig config, WebApiComponents? components = null)
    {
        ThrowIfDisposed();
        if (config == null)
            throw new ArgumentNullException(nameof(config));

        return GetOrAddTyped<ISoapClient>(name, key => new SoapClient(key, config, loggerFactory.CreateLogger<SoapClient>(), components));
    }

    // 名称已经注册成另一种客户端时，既不能返回它（类型不符），也不能悄悄替换它：报错并说明现有实例的类型。
    private TClient GetOrAddTyped<TClient>(string name, Func<string, IWebApiEndpoint> create) where TClient : class, IWebApiEndpoint
    {
        IWebApiEndpoint registered = GetOrAddCore(name, create);
        if (registered is TClient typed)
            return typed;

        throw new InvalidOperationException($"The name '{name}' is already registered with a {registered.GetType().Name}, which is not a {typeof(TClient).Name}.");
    }
}
