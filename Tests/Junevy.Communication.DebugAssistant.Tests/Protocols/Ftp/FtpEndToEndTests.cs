using System.IO;
using System.Text;
using Junevy.Communication.DebugAssistant.Protocols.Ftp;
using Junevy.Communication.DebugAssistant.Sessions;
using Junevy.Communication.DebugAssistant.Shell;
using Junevy.Communication.DebugAssistant.Tests.Support;
using Junevy.Communication.Ftp;
using Junevy.Communication.Testing.Servers;

namespace Junevy.Communication.DebugAssistant.Tests.Protocols.Ftp;

/// <summary>
/// FTP 页与进程内 FTP 模拟服务器（Task 16 的 <see cref="FakeFtpServer"/>）的端到端测试（计划 Task 19 第 2 条）：列目录；上传与下载及进度；
/// Skip 模式；新建目录（逐级创建）；递归删除（先确认，测试替身回答）；重命名；GBK 编码的文件名。
/// </summary>
public sealed class FtpEndToEndTests : IDisposable
{
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(10);
    private static readonly DateTime Modified = new(2026, 10, 20, 8, 0, 0, DateTimeKind.Utc);
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "junevy-da-ftp-" + Guid.NewGuid().ToString("N"));

    public FtpEndToEndTests()
    {
        Directory.CreateDirectory(_directory);
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }

    private static FtpSessionViewModel CreatePage(
        FakeFtpServer server,
        ManualUiDispatcher dispatcher,
        FakeFileDialogService? files = null,
        FakeConfirmService? confirm = null)
    {
        var page = new FtpSessionViewModel(ProtocolCatalog.Get(ProtocolKind.Ftp), FakeServices.Create(dispatcher, files: files, confirm: confirm));
        page.Host = "127.0.0.1";
        page.Port = server.Port;
        page.UserName = "line1";
        page.Password = "secret";
        return page;
    }

    private string WriteLocal(string name, byte[] content)
    {
        string path = Path.Combine(_directory, name);
        File.WriteAllBytes(path, content);
        return path;
    }

    [Fact]
    public async Task Listing_ShowsTheDirectoriesFirst_ThenTheFilesWithTheirSizes()
    {
        await using var server = FakeFtpServer.Start();
        server.FileSystem.CreateDirectory("/recipes");
        server.FileSystem.WriteFile("/readme.txt", Encoding.UTF8.GetBytes("hello"), Modified);
        await using var page = CreatePage(server, new ManualUiDispatcher());

        await page.ConnectCommand.ExecuteAsync(null);

        Assert.Equal((SessionState.Connected, "recipes"), (page.State, page.Entries[0].Name));
        Assert.Contains(page.Entries, row => row.Name == "recipes" && row.IsDirectory && row.Type == "目录");
        Assert.Contains(page.Entries, row => row.Name == "readme.txt" && !row.IsDirectory && row.Size == "5");
        Assert.Equal("/ 中有 2 项。", page.OperationMessage);
    }

    [Fact]
    public async Task UploadAndDownload_TransferTheBytes_AndReportProgress()
    {
        await using var server = FakeFtpServer.Start();
        byte[] content = Enumerable.Range(0, 200_000).Select(i => (byte)(i % 251)).ToArray();
        var files = new FakeFileDialogService { OpenFrom = WriteLocal("upload.bin", content) };
        await using var page = CreatePage(server, new ManualUiDispatcher(), files);
        await page.ConnectCommand.ExecuteAsync(null);

        await page.UploadCommand.ExecuteAsync(null);

        Assert.Equal(content, server.FileSystem.ReadFile("/upload.bin"));
        Assert.StartsWith("已传输：/upload.bin", page.OperationMessage, StringComparison.Ordinal);
        Assert.InRange(page.ProgressValue, 99, 100);

        string downloaded = Path.Combine(_directory, "downloaded.bin");
        files.SaveTo = downloaded;
        page.SelectedEntry = page.Entries.Single(row => row.Name == "upload.bin");
        await page.DownloadCommand.ExecuteAsync(null);

        Assert.Equal(content, File.ReadAllBytes(downloaded));
        Assert.StartsWith("已传输：", page.OperationMessage, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SkipMode_LeavesAnExistingTargetUntouched()
    {
        await using var server = FakeFtpServer.Start();
        server.FileSystem.WriteFile("/keep.txt", Encoding.UTF8.GetBytes("old"), Modified);
        var files = new FakeFileDialogService { OpenFrom = WriteLocal("keep.txt", Encoding.UTF8.GetBytes("new")) };
        await using var page = CreatePage(server, new ManualUiDispatcher(), files);
        await page.ConnectCommand.ExecuteAsync(null);
        page.ExistsMode = FtpExistsMode.Skip;

        await page.UploadCommand.ExecuteAsync(null);

        Assert.Equal("old", Encoding.UTF8.GetString(server.FileSystem.ReadFile("/keep.txt")));
        Assert.StartsWith("目标已存在，已跳过：/keep.txt", page.OperationMessage, StringComparison.Ordinal);
    }

    [Fact]
    public async Task UploadRefusedByTheServer_KeepsItsFailure_AfterTheListingRefreshes()
    {
        await using var server = FakeFtpServer.Start();
        server.Faults.FailCommand("STOR", 552);
        var files = new FakeFileDialogService { OpenFrom = WriteLocal("refused.bin", Encoding.UTF8.GetBytes("x")) };
        await using var page = CreatePage(server, new ManualUiDispatcher(), files);
        await page.ConnectCommand.ExecuteAsync(null);

        await page.UploadCommand.ExecuteAsync(null);

        OperationRecord upload = page.Operations.Single(record => record.Operation.Contains("上传", StringComparison.Ordinal));
        Assert.False(upload.Succeeded);
        Assert.False(server.FileSystem.FileExists("/refused.bin"));
        Assert.Equal(ResultText.Failure(upload.ErrorKind, upload.Message), page.OperationMessage);
    }

    [Fact]
    public async Task EnterDirectory_WhenItCannotBeListed_StaysInThePreviousDirectory()
    {
        await using var server = FakeFtpServer.Start();
        server.FileSystem.CreateDirectory("/locked");
        await using var page = CreatePage(server, new ManualUiDispatcher());
        await page.ConnectCommand.ExecuteAsync(null);
        page.SelectedEntry = page.Entries.Single(row => row.Name == "locked");
        // 550 会被 SDK 当作目录不存在而返回空列表（见 FtpSdkSession.ListAsync），因此用 421 模拟列出失败；故障在连接之后设置。
        server.Faults.FailCommand("MLSD", 421);
        server.Faults.FailCommand("LIST", 421);

        await page.EnterDirectoryCommand.ExecuteAsync(page.SelectedEntry);

        OperationRecord failed = page.Operations.Single(record => !record.Succeeded);
        Assert.Equal("/", page.CurrentDirectory);
        Assert.Contains(page.Entries, row => row.Name == "locked" && row.IsDirectory);
        Assert.Equal(ResultText.Failure(failed.ErrorKind, failed.Message), page.OperationMessage);
    }

    [Fact]
    public async Task CreateDirectory_MakesEveryMissingLevel()
    {
        await using var server = FakeFtpServer.Start();
        await using var page = CreatePage(server, new ManualUiDispatcher());
        await page.ConnectCommand.ExecuteAsync(null);
        page.NewDirectoryName = "projects/2026";

        await page.CreateDirectoryCommand.ExecuteAsync(null);

        Assert.True(server.FileSystem.DirectoryExists("/projects/2026"));
        Assert.Contains(page.Entries, row => row.Name == "projects" && row.IsDirectory);
        Assert.Equal(string.Empty, page.NewDirectoryName);
    }

    [Fact]
    public async Task RecursiveDelete_NeedsConfirmation_AndDoesNothingWhenDeclined()
    {
        await using var server = FakeFtpServer.Start();
        server.FileSystem.CreateDirectory("/old");
        server.FileSystem.WriteFile("/old/a.txt", Encoding.UTF8.GetBytes("a"), Modified);
        var confirm = new FakeConfirmService { Answer = false };
        await using var page = CreatePage(server, new ManualUiDispatcher(), confirm: confirm);
        await page.ConnectCommand.ExecuteAsync(null);
        page.SelectedEntry = page.Entries.Single(row => row.Name == "old");

        await page.DeleteDirectoryCommand.ExecuteAsync(null);

        Assert.Contains(confirm.Asked, message => message.Contains("/old", StringComparison.Ordinal));
        Assert.True(server.FileSystem.DirectoryExists("/old"));
        Assert.Equal("已取消删除。", page.OperationMessage);

        confirm.Answer = true;
        await page.DeleteDirectoryCommand.ExecuteAsync(null);

        Assert.False(server.FileSystem.DirectoryExists("/old"));
        Assert.StartsWith("已删除目录 /old", page.OperationMessage, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Rename_MovesTheSelectedEntry_WithinItsDirectory()
    {
        await using var server = FakeFtpServer.Start();
        server.FileSystem.WriteFile("/draft.txt", Encoding.UTF8.GetBytes("d"), Modified);
        await using var page = CreatePage(server, new ManualUiDispatcher());
        await page.ConnectCommand.ExecuteAsync(null);
        page.SelectedEntry = page.Entries.Single(row => row.Name == "draft.txt");
        page.RenameTo = "final.txt";

        await page.RenameCommand.ExecuteAsync(null);

        Assert.Equal((true, false), (server.FileSystem.FileExists("/final.txt"), server.FileSystem.FileExists("/draft.txt")));
    }

    [Fact]
    public async Task GbkEncodedNames_AreListed_WhenTheEncodingIsSet()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        Encoding gbk = Encoding.GetEncoding("GBK");
        await using var server = FakeFtpServer.Start(new FakeFtpOptions { FileNameEncoding = gbk, AdvertiseUtf8 = false });
        server.FileSystem.CreateDirectory("/资料");
        await using var page = CreatePage(server, new ManualUiDispatcher());
        page.EncodingName = "GBK";

        await page.ConnectCommand.ExecuteAsync(null);

        Assert.Contains(page.Entries, row => row.Name == "资料" && row.IsDirectory);
    }
}
