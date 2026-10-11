using Junevy.Communication.Core.Results;
using Microsoft.Extensions.Logging;

namespace Junevy.Communication.Channels.Lifecycle;

/// <summary>
/// 客户端的心跳探测来源（P25 Task 11，补审第 1 节）：代码级探测（<see cref="ClientOptionsCore.HealthProbe"/>）或工厂探测。
/// 工厂探测以通道自身为参数调用一次，结果保留到通道释放，之后的重连复用它；工厂抛出异常或返回 null 时本次打开失败。
/// 字节流与数据报的驱动各持有一个实例；仅由 OpenAsync 访问，OpenAsync 由生命周期锁串行化。
/// </summary>
internal sealed class ClientProbeSource
{
    private readonly ClientOptionsCore core;
    private readonly IByteChannel owner;
    private readonly ConnectionStatistics statistics;
    private readonly ILogger logger;
    private readonly Func<string> describeEndpoint;
    private IHealthProbe? factoryProbe;

    /// <summary>创建探测来源。</summary>
    /// <param name="core">已校验的客户端运行参数。</param>
    /// <param name="owner">公开的通道实例；<see cref="ChannelComponents.HealthProbeFactory"/> 以它为参数调用。</param>
    /// <param name="statistics">连接统计。</param>
    /// <param name="logger">日志记录器。</param>
    /// <param name="describeEndpoint">日志用的端点描述。</param>
    public ClientProbeSource(ClientOptionsCore core, IByteChannel owner, ConnectionStatistics statistics, ILogger logger,
                             Func<string> describeEndpoint)
    {
        this.core = core;
        this.owner = owner;
        this.statistics = statistics;
        this.logger = logger;
        this.describeEndpoint = describeEndpoint;
    }

    /// <summary>
    /// 解析工厂探测（仅启用心跳且配置了工厂时）。已解析过则直接成功，之后的重连复用同一个探测。
    /// </summary>
    /// <returns>成功，或工厂失败时的失败结果（打开随之失败）。</returns>
    public CommResult Resolve()
    {
        if (!core.Heartbeat.Enabled || core.HealthProbeFactory == null || factoryProbe != null)
            return CommResult.Success();

        IHealthProbe? created;
        try
        {
            created = core.HealthProbeFactory(owner);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "HealthProbeFactory threw an exception; the connection to {Endpoint} was not opened.", describeEndpoint());
            return CommResult.Fail("HealthProbeFactory threw an exception; the connection was not opened.", CommErrorKind.Unspecified, null, ex);
        }

        if (created == null)
        {
            logger.LogWarning("HealthProbeFactory returned null; the connection to {Endpoint} was not opened.", describeEndpoint());
            return CommResult.Fail("HealthProbeFactory returned null; the connection was not opened.", CommErrorKind.Unspecified);
        }

        factoryProbe = created;
        return CommResult.Success();
    }

    /// <summary>
    /// 创建本连接的心跳监视器；既未启用心跳也没有空闲超时时为 null。调用方负责启动它。
    /// 心跳与空闲监视只在握手完成后启动；使用同一个尝试对象报告死亡。
    /// </summary>
    /// <param name="attempt">本连接的尝试对象。</param>
    /// <returns>监视器，或 null。</returns>
    public HeartbeatMonitor? CreateHeartbeat(ConnectionAttempt attempt)
    {
        if (!core.Heartbeat.Enabled && core.IdleTimeout <= 0)
            return null;

        return new HeartbeatMonitor(core.HealthProbe ?? factoryProbe, core.Heartbeat, core.IdleTimeout, statistics,
                                    reason => attempt.Report(reason, null), logger);
    }
}
