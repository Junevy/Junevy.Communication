using Junevy.Communication.Core.Registry;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Junevy.Communication.Ftp.DependencyInjection;

/// <summary>
/// FTP 客户端工厂：按名称创建、获取与移除客户端，同一个名称返回同一个实例。
/// 行为与 <c>ChannelFactory</c>、<c>PlcClientFactory</c>、<c>WebApiClientFactory</c>、<c>MqttClientFactory</c>、<c>OpcUaClientFactory</c> 一致。
/// </summary>
/// <remarks>
/// 名称、别名与释放由 <see cref="NamedFactory{TItem}"/> 提供（底层为 <see cref="NamedRegistry{T}"/>）：
/// 同名获取返回同一实例，别名与其目标共享实例，移除或释放时释放客户端。配置非法时由
/// <see cref="FtpClient.Create"/> 抛出 <see cref="ArgumentException"/>。
/// 工厂释放之后，除 <see cref="NamedFactory{TItem}.Dispose"/> 与 <see cref="NamedFactory{TItem}.DisposeAsync"/> 外的成员都抛出
/// <see cref="ObjectDisposedException"/>。
/// </remarks>
public sealed class FtpClientFactory : NamedFactory<FtpClient>
{
    private readonly ILoggerFactory loggerFactory;

    /// <summary>创建客户端工厂。</summary>
    /// <param name="loggerFactory">日志工厂；为 null 时不输出日志。</param>
    public FtpClientFactory(ILoggerFactory? loggerFactory = null)
        : base((loggerFactory ?? NullLoggerFactory.Instance).CreateLogger<FtpClientFactory>())
    {
        this.loggerFactory = loggerFactory ?? NullLoggerFactory.Instance;
    }

    /// <summary>
    /// 按名称获取 FTP 客户端；名称不存在时创建并注册。名称已存在时直接返回已有实例，
    /// <paramref name="options"/> 不生效——因此之后修改这个选项对象也不会影响已创建的客户端（取的是创建时的快照）。
    /// </summary>
    /// <param name="name">客户端名称或别名；不能为 null 或空。</param>
    /// <param name="options">连接选项；不能为 null。</param>
    /// <returns>已注册（或刚创建）的客户端。</returns>
    /// <exception cref="ArgumentNullException"><paramref name="options"/> 为 null。</exception>
    /// <exception cref="ArgumentException"><paramref name="name"/> 为 null 或空，或选项非法。</exception>
    /// <exception cref="ObjectDisposedException">工厂已释放。</exception>
    public FtpClient GetOrAdd(string name, FtpClientOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        return GetOrAddCore(name, key => FtpClient.Create(options, loggerFactory));
    }
}
