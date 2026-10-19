using System.Reflection;
using Junevy.Communication.DebugAssistant.Tests.Support;

namespace Junevy.Communication.DebugAssistant.Tests.Architecture;

/// <summary>
/// U2（计划第 3 节）：视图模型、服务、会话、日志、工作区与协议层不能引用视图相关的程序集；
/// 模拟器不能引用任何 WPF 程序集或协议包（模拟器的规则在 Task 11 加入）。
/// </summary>
public sealed class LayeringRulesTests
{
    private const string AppNamespace = "Junevy.Communication.DebugAssistant";

    /// <summary>不允许出现在这些命名空间的类型引用里。</summary>
    private static readonly string[] ForbiddenInPresentationFreeNamespaces =
    [
        "PresentationFramework",
        "PresentationCore",
        "WindowsBase",
        "Junevy.Controls",
        "Microsoft.Xaml.Behaviors",
    ];

    /// <summary>不含视图的命名空间（计划 U2 与第 4.2 节的目录）。</summary>
    private static readonly string[] PresentationFreeNamespaces =
    [
        $"{AppNamespace}.Sessions",
        $"{AppNamespace}.Services",
        $"{AppNamespace}.Logging",
        $"{AppNamespace}.Workspace",
        $"{AppNamespace}.Common",
        $"{AppNamespace}.Protocols",
    ];

    [Fact]
    public void PresentationFreeNamespaces_DoNotReferenceTheViewAssemblies()
    {
        Assembly app = typeof(App).Assembly;
        var violations = new List<string>();

        foreach (Type type in app.GetTypes().Where(t => t.DeclaringType is null && InAny(t.Namespace, PresentationFreeNamespaces)))
        {
            foreach (string referenced in AssemblyReferenceScanner.Of(type))
            {
                if (ForbiddenInPresentationFreeNamespaces.Any(forbidden => referenced == forbidden))
                {
                    violations.Add($"{type.FullName} -> {referenced}");
                }
            }
        }

        Assert.Empty(violations);
    }

    [Fact]
    public void PresentationFreeNamespaces_ContainTypes()
    {
        // 守卫本身：如果命名空间改名，上面的测试会空转而不是报告成功。
        Assembly app = typeof(App).Assembly;

        foreach (string ns in PresentationFreeNamespaces.Where(n => n.EndsWith(".Sessions", StringComparison.Ordinal)
                     || n.EndsWith(".Services", StringComparison.Ordinal)
                     || n.EndsWith(".Logging", StringComparison.Ordinal)))
        {
            Assert.Contains(app.GetTypes(), t => t.Namespace == ns);
        }
    }

    private static bool InAny(string? ns, string[] prefixes) =>
        ns is not null && prefixes.Any(prefix => ns == prefix || ns.StartsWith(prefix + ".", StringComparison.Ordinal));
}
