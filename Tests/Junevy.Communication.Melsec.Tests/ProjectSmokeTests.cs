using System.Reflection;
using System.Runtime.InteropServices;
using Junevy.Communication.Serial;
using Junevy.Communication.Tcp;
using Junevy.Communication.Udp;

namespace Junevy.Communication.Melsec.Tests;

/// <summary>
/// 证明测试确实在声明的目标框架上执行，Melsec 程序集已被引用，并且 Tcp、Udp、Serial 程序集可以从测试项目直接使用。
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
    public void MelsecAssembly_LoadsFromReference()
    {
        // 尚无 Melsec 的公开类型时以程序集加载证明项目引用与输出正常（缺少项目引用时此处失败）。
        Assembly melsec = Assembly.Load("Junevy.Communication.Melsec");

        Assert.Equal("Junevy.Communication.Melsec", melsec.GetName().Name);
    }

    [Fact]
    public void ReferencesTransportAssemblies()
    {
        Assert.Equal("Junevy.Communication.Tcp", typeof(TcpClientChannelConfig).Assembly.GetName().Name);
        Assert.Equal("Junevy.Communication.Udp", typeof(UdpChannelConfig).Assembly.GetName().Name);
        Assert.Equal("Junevy.Communication.Serial", typeof(SerialChannelConfig).Assembly.GetName().Name);
    }
}
