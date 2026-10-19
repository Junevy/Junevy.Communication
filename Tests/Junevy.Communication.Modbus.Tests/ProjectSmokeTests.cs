using System.Reflection;

namespace Junevy.Communication.Modbus.Tests;

/// <summary>
/// 项目骨架（P7-D1）：被测程序集的标识与版本，以及测试项目确实引用了它。
/// </summary>
public sealed class ProjectSmokeTests
{
    [Fact(Timeout = 20000)]
    public void ModbusAssembly_HasTheVersionOfThePlcSuiteRelease()
    {
        Assembly modbus = Assembly.Load("Junevy.Communication.Modbus");

        Assert.Equal(new Version(3, 0, 0, 0), modbus.GetName().Version);
    }

    [Fact(Timeout = 20000)]
    public void ModbusAssembly_InformationalVersionIsThePreviewTag()
    {
        Assembly modbus = Assembly.Load("Junevy.Communication.Modbus");
        string? informational = modbus.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;

        Assert.NotNull(informational);
        Assert.StartsWith("3.0.0-preview.1", informational, StringComparison.Ordinal);
    }
}
