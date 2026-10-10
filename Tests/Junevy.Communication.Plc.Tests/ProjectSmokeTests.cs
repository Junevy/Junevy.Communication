using System.Reflection;
using System.Runtime.InteropServices;
using Junevy.Communication.Channels;

namespace Junevy.Communication.Plc.Tests;

/// <summary>
/// 证明测试确实在声明的目标框架上执行，并且 Plc 程序集已被引用、依赖的 Channels 程序集可以从测试项目直接使用。
/// </summary>
public sealed class ProjectSmokeTests
{
    [Fact]
    public void Runtime_MatchesTargetFramework()
    {
#if NETFRAMEWORK
        Assert.StartsWith(".NET Framework", RuntimeInformation.FrameworkDescription, StringComparison.Ordinal);
#elif NET8_0
        Assert.StartsWith(".NET 8", RuntimeInformation.FrameworkDescription, StringComparison.Ordinal);
#else
#error Unexpected target framework.
#endif
    }

    [Fact]
    public void PlcAssembly_LoadsFromReference()
    {
        // 尚无 Plc 的公开类型时以程序集加载证明项目引用与输出正常（缺少项目引用时此处失败）。
        Assembly plc = Assembly.Load("Junevy.Communication.Plc");

        Assert.Equal("Junevy.Communication.Plc", plc.GetName().Name);
    }

    [Fact]
    public void ReferencesChannelsAssembly()
    {
        Assert.Equal("Junevy.Communication.Channels", typeof(IClientChannel).Assembly.GetName().Name);
    }
}
