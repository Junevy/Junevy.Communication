using Microsoft.Extensions.Logging;

namespace Junevy.Communication.WebApi.Factory;

/// <summary>
/// 无 DI 容器的宿主（例如 Prism）使用的 <see cref="WebApiClientFactory"/> 构建器，用法与 <c>ChannelFactoryBuilder</c>、<c>PlcClientFactoryBuilder</c> 一致。
/// </summary>
public sealed class WebApiClientFactoryBuilder
{
    private ILoggerFactory? loggerFactory;

    private WebApiClientFactoryBuilder()
    {
    }

    /// <summary>创建新的构建器。</summary>
    /// <returns>新的构建器实例。</returns>
    public static WebApiClientFactoryBuilder Create() => new WebApiClientFactoryBuilder();

    /// <summary>设置日志工厂；未设置时工厂不输出日志。</summary>
    /// <param name="loggerFactory">日志工厂。</param>
    /// <returns>当前构建器，便于链式调用。</returns>
    /// <exception cref="ArgumentNullException"><paramref name="loggerFactory"/> 为 null。</exception>
    public WebApiClientFactoryBuilder WithLoggerFactory(ILoggerFactory loggerFactory)
    {
        this.loggerFactory = loggerFactory ?? throw new ArgumentNullException(nameof(loggerFactory));
        return this;
    }

    /// <summary>构建客户端工厂。每次调用返回新的独立实例。</summary>
    /// <returns>新的客户端工厂。</returns>
    public WebApiClientFactory Build() => new WebApiClientFactory(loggerFactory);
}
