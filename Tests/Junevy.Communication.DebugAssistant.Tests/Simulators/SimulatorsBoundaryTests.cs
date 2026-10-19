using System.Runtime.CompilerServices;
using Junevy.Communication.DebugAssistant.Simulators.Hosting;

namespace Junevy.Communication.DebugAssistant.Tests.Simulators;

/// <summary>
/// DA-D14 的边界：模拟器是独立实现，不引用任何协议包，不引用 WPF，也不向测试项目开放内部成员。
/// </summary>
public sealed class SimulatorsBoundaryTests
{
    private static readonly string[] ForbiddenReferences =
    [
        "Junevy.Communication.Modbus",
        "Junevy.Communication.Melsec",
        "Junevy.Communication.Siemens",
        "Junevy.Communication.Omron",
        "Junevy.Communication.Plc",
        "PresentationFramework",
        "PresentationCore",
        "WindowsBase",
        "Junevy.Controls",
    ];

    [Fact]
    public void Simulators_DoNotReferenceProtocolPackagesOrWpf()
    {
        string[] referenced = typeof(SimulatorHost).Assembly.GetReferencedAssemblies().Select(name => name.Name ?? string.Empty).ToArray();

        Assert.Empty(referenced.Where(ForbiddenReferences.Contains));
    }

    [Fact]
    public void Simulators_DoNotOpenTheirInternalsToTheTests()
    {
        Assert.Empty(typeof(SimulatorHost).Assembly.GetCustomAttributes(typeof(InternalsVisibleToAttribute), inherit: false));
    }
}
