using Junevy.Communication.DebugAssistant.Shell;
using Microsoft.Extensions.DependencyInjection;

namespace Junevy.Communication.DebugAssistant.Sessions;

/// <summary>从 DI 中按种类解析键控的 <see cref="SessionViewModel"/>，并分配默认名称。</summary>
internal sealed class SessionFactory : ISessionFactory
{
    private readonly IServiceProvider _services;
    private readonly SessionNamer _namer;

    public SessionFactory(IServiceProvider services, SessionNamer namer)
    {
        _services = services;
        _namer = namer;
    }

    public SessionViewModel Create(ProtocolKind kind)
    {
        SessionViewModel session = _services.GetRequiredKeyedService<SessionViewModel>(kind);
        session.Title = _namer.Next(session.Descriptor);
        return session;
    }
}
