using System.Reflection;
using System.Runtime.InteropServices;

namespace Junevy.Communication.Mqtt.Tests;

/// <summary>
/// 项目引用与目标框架的冒烟测试（与 Melsec.Tests、Plc.Tests 同法）。
/// </summary>
public sealed class ProjectSmokeTests
{
    [Fact]
    public void MqttAssembly_LoadsFromReference()
    {
        // Mqtt 包还没有公开类型时以程序集加载证明项目引用与输出正常（缺少项目引用时此处失败）。
        Assembly mqtt = Assembly.Load("Junevy.Communication.Mqtt");

        Assert.Equal("Junevy.Communication.Mqtt", mqtt.GetName().Name);
    }

    [Fact]
    public void TargetFramework_IsNet8Only()
    {
        // P6-C1：三个 SDK 适配包不做 net472。此处的断言防止有人把 net8.0 覆盖误删后测试仍然"通过"。
        Assert.StartsWith(".NET 8.", RuntimeInformation.FrameworkDescription, StringComparison.Ordinal);
    }
}