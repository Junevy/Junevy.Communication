using System.Reflection;
using System.Runtime.InteropServices;

namespace Junevy.Communication.Ftp.Tests;

/// <summary>
/// 项目引用与目标框架的冒烟测试（与 Mqtt.Tests、OpcUa.Tests 同法）。
/// </summary>
public sealed class ProjectSmokeTests
{
    [Fact]
    public void FtpAssembly_LoadsFromReference()
    {
        // 缺少项目引用时此处失败；Ftp 包的公开类型在后续 Task 中加入。
        Assembly ftp = Assembly.Load("Junevy.Communication.Ftp");

        Assert.Equal("Junevy.Communication.Ftp", ftp.GetName().Name);
    }

    [Fact]
    public void TargetFramework_IsNet8Only()
    {
        // P6-C1：SDK 适配包不做 net472。此处的断言防止有人把 net8.0 覆盖误删后测试仍然"通过"。
        Assert.StartsWith(".NET 8.", RuntimeInformation.FrameworkDescription, StringComparison.Ordinal);
    }
}
