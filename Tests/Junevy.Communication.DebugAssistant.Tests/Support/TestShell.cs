using Junevy.Communication.DebugAssistant.Hosting;
using Junevy.Communication.DebugAssistant.Sessions;
using Junevy.Communication.DebugAssistant.Shell;
using Junevy.Communication.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Junevy.Communication.DebugAssistant.Tests.Support;

/// <summary>构造与应用相同的 DI 图；测试可以覆盖个别注册（后注册者生效）。</summary>
internal static class TestShell
{
    /// <summary>返回服务提供程序与测试日志（所有日志写入 <see cref="TestLogger"/>）。</summary>
    public static (ServiceProvider Services, TestLogger Log) Create(Action<IServiceCollection>? overrides = null)
    {
        var log = new TestLogger();
        var services = new ServiceCollection();
        services.AddSingleton<ILoggerFactory>(log);
        services.AddShellServices();
        overrides?.Invoke(services);
        return (services.BuildServiceProvider(), log);
    }

    /// <summary>为指定种类注册可追踪的页签；返回的列表记录每次创建的实例。</summary>
    public static List<TrackingSession> TrackKind(IServiceCollection services, ProtocolKind kind)
    {
        var created = new List<TrackingSession>();
        ProtocolDescriptor descriptor = ProtocolCatalog.Get(kind);
        services.AddKeyedTransient<SessionViewModel>(kind, (_, _) =>
        {
            var session = new TrackingSession(descriptor);
            created.Add(session);
            return session;
        });
        return created;
    }
}
