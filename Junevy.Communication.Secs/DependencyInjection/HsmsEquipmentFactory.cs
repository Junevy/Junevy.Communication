using Junevy.Communication.Core.Registry;
using Junevy.Communication.Secs.Hsms;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Junevy.Communication.Secs.DependencyInjection;

/// <summary>
/// 设备端工厂：按名称创建、获取与移除 <see cref="HsmsEquipment"/>。行为与 <c>PlcClientFactory</c>、<c>ChannelFactory</c> 一致。
/// </summary>
/// <remarks>
/// 名称、别名与释放由 <see cref="NamedFactory{TItem}"/> 提供（底层为 <c>NamedRegistry</c>）：同名获取返回同一实例，
/// 别名与其目标共享实例，移除或释放工厂时释放设备端（监听随之停止）。选项非法时 <see cref="HsmsEquipment.Create"/>
/// 抛出 <see cref="ArgumentException"/>，此时不注册任何东西。工厂释放之后，除 <c>Dispose</c> 与 <c>DisposeAsync</c>
/// 外的成员都抛出 <see cref="ObjectDisposedException"/>。
/// </remarks>
public sealed class HsmsEquipmentFactory : NamedFactory<HsmsEquipment>
{
    private readonly ILoggerFactory loggerFactory;

    /// <summary>创建设备端工厂。</summary>
    /// <param name="loggerFactory">日志工厂；为 null 时设备端不输出日志。</param>
    public HsmsEquipmentFactory(ILoggerFactory? loggerFactory = null)
        : base((loggerFactory ?? NullLoggerFactory.Instance).CreateLogger<HsmsEquipmentFactory>())
    {
        this.loggerFactory = loggerFactory ?? NullLoggerFactory.Instance;
    }

    /// <summary>
    /// 按名称获取设备端；名称不存在时按 <paramref name="options"/> 创建并注册（<c>Create</c> 只建对象，不监听也不连接）。
    /// 名称已存在时直接返回已有实例，<paramref name="options"/> 不生效。
    /// </summary>
    /// <param name="name">设备端名称或别名；不能为 null 或空。</param>
    /// <param name="options">设备端选项；校验在 <see cref="HsmsEquipment.Create"/> 里。</param>
    /// <returns>已注册（或刚创建）的设备端。</returns>
    /// <exception cref="ArgumentNullException"><paramref name="options"/> 为 null。</exception>
    /// <exception cref="ArgumentException"><paramref name="name"/> 为 null 或空，或选项非法。</exception>
    /// <exception cref="ObjectDisposedException">工厂已释放。</exception>
    public HsmsEquipment GetOrAdd(string name, HsmsEquipmentOptions options)
    {
        ThrowIfDisposed();
        if (options == null)
            throw new ArgumentNullException(nameof(options));

        // 选项在 Create 里取快照，因此调用方之后改它不影响已建好的设备端。
        return GetOrAddCore(name, _ => HsmsEquipment.Create(options, loggerFactory));
    }
}