using Microsoft.Extensions.Logging;

namespace Junevy.Communication.WebApi.Server;

/// <summary>
/// 无 DI 容器的宿主使用的 <see cref="WebApiServerFactory"/> 构建器，用法与 <c>WebApiClientFactoryBuilder</c> 一致（P32-D14）。
/// </summary>
public sealed class WebApiServerFactoryBuilder
{
    private ILoggerFactory? loggerFactory;

    private WebApiServerFactoryBuilder()
    {
    }

    /// <summary>创建新的构建器。</summary>
    /// <returns>新的构建器实例。</returns>
    public static WebApiServerFactoryBuilder Create() => new WebApiServerFactoryBuilder();

    /// <summary>设置日志工厂；未设置时工厂不输出日志。</summary>
    /// <param name="loggerFactory">日志工厂。</param>
    /// <returns>当前构建器，便于链式调用。</returns>
    /// <exception cref="ArgumentNullException"><paramref name="loggerFactory"/> 为 null。</exception>
    public WebApiServerFactoryBuilder WithLoggerFactory(ILoggerFactory loggerFactory)
    {
        ArgumentNullException.ThrowIfNull(loggerFactory);
        this.loggerFactory = loggerFactory;
        return this;
    }

    /// <summary>构建服务器工厂。每次调用返回新的独立实例。</summary>
    /// <returns>新的服务器工厂。</returns>
    public WebApiServerFactory Build() => new WebApiServerFactory(loggerFactory);
}
