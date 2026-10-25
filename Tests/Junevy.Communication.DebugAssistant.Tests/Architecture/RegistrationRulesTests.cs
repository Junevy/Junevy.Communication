using System.Windows;
using Junevy.Communication.DebugAssistant.Sessions;
using Junevy.Communication.DebugAssistant.Shell;
using Junevy.Communication.DebugAssistant.Tests.Support;
using Microsoft.Extensions.DependencyInjection;

namespace Junevy.Communication.DebugAssistant.Tests.Architecture;

/// <summary>
/// DA-D19（计划第 0 节）：新增一种页签的成本固定，所有登记都必须齐全。
/// 参数类型（<c>SessionSettings</c> 的派生类型）由 Task 3 加入检查。
/// </summary>
public sealed class RegistrationRulesTests
{
    [Fact]
    public void EveryProtocolKind_HasADescriptor()
    {
        Assert.Equal(Enum.GetValues<ProtocolKind>().Length, ProtocolCatalog.All.Count);
        foreach (ProtocolKind kind in Enum.GetValues<ProtocolKind>())
        {
            Assert.Equal(kind, ProtocolCatalog.Get(kind).Kind);
        }
    }

    [Fact]
    public async Task EveryProtocolKind_HasAKeyedSessionService()
    {
        var (services, _) = TestShell.Create();
        foreach (ProtocolKind kind in Enum.GetValues<ProtocolKind>())
        {
            SessionViewModel session = services.GetRequiredKeyedService<SessionViewModel>(kind);
            Assert.Equal(kind, session.Kind);
            await session.DisposeAsync();
        }
    }

    [Fact]
    public async Task EverySessionViewModelType_HasADataTemplate()
    {
        var (services, _) = TestShell.Create();
        var types = new HashSet<Type>();
        foreach (ProtocolKind kind in Enum.GetValues<ProtocolKind>())
        {
            SessionViewModel session = services.GetRequiredKeyedService<SessionViewModel>(kind);
            types.Add(session.GetType());
            await session.DisposeAsync();
        }

        UiThread.Invoke(() =>
        {
            foreach (Type type in types)
            {
                Assert.IsType<DataTemplate>(Application.Current.TryFindResource(new DataTemplateKey(type)));
            }
        });
    }
}
