using Junevy.Communication.DebugAssistant.Hosting;
using Junevy.Communication.DebugAssistant.Services;
using Junevy.Communication.DebugAssistant.Sessions;
using Junevy.Communication.DebugAssistant.Shell;
using Junevy.Communication.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Junevy.Communication.DebugAssistant.Tests.Support;

/// <summary>构造与应用相同的 DI 图；界面派发与时钟换成测试实现，日志写入 <see cref="TestLogger"/>。</summary>
internal static class TestShell
{
    /// <summary>
    /// 返回服务提供程序与测试日志。界面派发是 <see cref="ManualUiDispatcher"/>，时钟是 <see cref="FakeClock"/>，
    /// 可以用 <c>GetRequiredService</c> 取回。<paramref name="overrides"/> 在默认注册之后执行，后注册者生效。
    /// </summary>
    public static (ServiceProvider Services, TestLogger Log) Create(Action<IServiceCollection>? overrides = null)
    {
        var log = new TestLogger();
        var services = new ServiceCollection();
        services.AddSingleton<ILoggerFactory>(log);
        services.AddShellServices();
        services.AddSingleton<IUiDispatcher>(new ManualUiDispatcher());
        services.AddSingleton<TimeProvider>(new FakeClock());
        services.AddSingleton<IFileDialogService>(new FakeFileDialogService());
        services.AddSingleton<INotificationService>(new RecordingNotificationService());
        overrides?.Invoke(services);
        return (services.BuildServiceProvider(), log);
    }

    /// <summary>创建一个页签（占位实现，只提供日志等公共能力）。调用方负责释放。</summary>
    public static SessionViewModel CreateSession(ProtocolKind kind = ProtocolKind.Modbus)
    {
        var (services, _) = Create();
        return services.GetRequiredKeyedService<SessionViewModel>(kind);
    }

    /// <summary>为指定种类注册可追踪的页签；返回的列表记录每次创建的实例。</summary>
    public static List<TrackingSession> TrackKind(IServiceCollection services, ProtocolKind kind)
    {
        var created = new List<TrackingSession>();
        ProtocolDescriptor descriptor = ProtocolCatalog.Get(kind);
        services.AddKeyedTransient<SessionViewModel>(kind, (provider, _) =>
        {
            var session = new TrackingSession(descriptor, provider.GetRequiredService<SessionServices>());
            created.Add(session);
            return session;
        });
        return created;
    }
}
