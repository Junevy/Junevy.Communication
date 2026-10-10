using System.Runtime.InteropServices;

namespace Junevy.Communication.Serial.Tests;

/// <summary>
/// 证明测试确实在声明的目标框架上执行：net472 目标运行在 .NET Framework 上，net8.0 目标运行在 .NET 8 上。
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
}
