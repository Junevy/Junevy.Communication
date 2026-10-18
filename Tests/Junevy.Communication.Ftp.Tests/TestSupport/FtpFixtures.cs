using System.Text;

namespace Junevy.Communication.Ftp.Tests.TestSupport;

/// <summary>
/// 列目录与文件操作用例的共同数据（计划 Task 4）：<c>/docs/recipe.txt</c>（12 字节，UTC 修改时间精确到秒）、
/// <c>/docs/archive/</c> 子目录与空目录 <c>/empty/</c>。
/// </summary>
public static class FtpFixtures
{
    /// <summary><c>/docs/recipe.txt</c> 的修改时间（UTC，精确到秒）。</summary>
    public static readonly DateTime RecipeModified = new(2026, 10, 12, 8, 30, 0, DateTimeKind.Utc);

    /// <summary>启动带有标准数据的模拟服务器。</summary>
    /// <param name="options">行为开关；为 null 时使用默认值。</param>
    /// <returns>已在监听的服务器。</returns>
    public static FakeFtpServer Start(FakeFtpOptions? options = null)
    {
        FakeFtpServer server = FakeFtpServer.Start(options);
        server.FileSystem.CreateDirectory("/docs/archive");
        server.FileSystem.CreateDirectory("/empty");
        server.FileSystem.WriteFile("/docs/recipe.txt", Encoding.ASCII.GetBytes("hello world!"), RecipeModified);
        return server;
    }
}
