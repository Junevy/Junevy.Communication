using System.Text;

namespace Junevy.Communication.Ftp.Tests;

/// <summary>
/// 编码名称的解析（P63-D12）：GBK 需要代码页提供程序，解析时已注册（计划 Task 2 第 4 条）。
/// </summary>
public sealed class FtpEncodingTests
{
    [Fact]
    public void Gbk_Resolves_ToCodePage936()
    {
        Encoding? encoding = FtpEncodings.Resolve("GBK");

        // Windows 代码页 936 就是 GBK；.NET 把它的 WebName 报告为 gb2312，因此断言代码页而不是名称。
        Assert.NotNull(encoding);
        Assert.Equal(936, encoding.CodePage);
    }

    [Fact]
    public void UnknownName_DoesNotResolve()
    {
        Assert.Null(FtpEncodings.Resolve("no-such-encoding"));
    }
}
