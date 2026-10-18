namespace Junevy.Communication.Ftp.Tests.TestSupport;

/// <summary>每个用例自己的临时目录（本地文件用例使用），释放时整体删除。</summary>
public sealed class TempDirectory : IDisposable
{
    /// <summary>创建一个唯一的临时目录。</summary>
    public TempDirectory()
    {
        Root = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "junevy-ftp-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Root);
    }

    /// <summary>临时目录的绝对路径。</summary>
    public string Root { get; }

    /// <summary>在临时目录下拼接路径。</summary>
    /// <param name="name">相对名称。</param>
    /// <returns>绝对路径。</returns>
    public string File(string name) => System.IO.Path.Combine(Root, name);

    /// <summary>删除临时目录（包括其中的文件）。</summary>
    public void Dispose()
    {
        try
        {
            Directory.Delete(Root, recursive: true);
        }
        catch (IOException)
        {
            // 测试结束时的清理是尽力而为的。
        }
        catch (UnauthorizedAccessException)
        {
            // 同上。
        }
    }
}
