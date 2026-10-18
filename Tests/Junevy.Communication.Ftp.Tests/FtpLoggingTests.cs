using Junevy.Communication.Core.Results;
using Junevy.Communication.Ftp.Tests.TestSupport;
using Junevy.Communication.Testing;
using Microsoft.Extensions.Logging;

namespace Junevy.Communication.Ftp.Tests;

/// <summary>
/// 日志（P63-D13、D15；Q40）：任何级别都不出现密码与文件内容；SDK 的日志经桥接进入同一个日志工厂，类别为 <c>Junevy.Communication.Ftp.Sdk</c>；
/// 每个操作记录操作名、远程路径、耗时与字节数，失败记 Warning（P6.3 审阅 R1）。
/// </summary>
[Collection(SocketTimingCollection.Name)]
public sealed class FtpLoggingTests
{
    private const string Secret = "s3cr3t-pass";

    [Fact]
    public async Task Password_IsNeverLogged_AtTraceLevel_IncludingTheSdkLogs()
    {
        await using FakeFtpServer server = FakeFtpServer.Start(new FakeFtpOptions { Password = Secret });
        using var loggers = new TestLogger();
        await using FtpClient client = FtpClient.Create(FtpTestClients.OptionsFor(server, password: Secret), loggers);

        CommResult result = await client.ConnectAsync();

        Assert.True(result.IsSuccess, result.ToString());
        Assert.NotEmpty(loggers.Entries.Where(e => e.CategoryName == "Junevy.Communication.Ftp.Sdk"));
        foreach (TestLogEntry entry in loggers.Entries)
        {
            Assert.DoesNotContain(Secret, entry.Message, StringComparison.Ordinal);
            Assert.DoesNotContain(Secret, entry.Exception?.ToString() ?? string.Empty, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task Transfers_NeverLogFileContent_AtTraceLevel()
    {
        const string marker = "content-marker-7f3a9c";
        await using FakeFtpServer server = FtpFixtures.Start();
        using var local = new TempDirectory();
        string source = local.File("up.txt");
        File.WriteAllText(source, marker);
        using var loggers = new TestLogger();
        await using FtpClient client = FtpClient.Create(FtpTestClients.OptionsFor(server), loggers);

        CommResult<FtpTransferStatus> uploaded = await client.UploadFileAsync(source, "/docs/up.txt");
        CommResult<FtpTransferStatus> downloaded = await client.DownloadFileAsync("/docs/up.txt", local.File("copy.txt"));

        Assert.True(uploaded.IsSuccess, uploaded.ToString());
        Assert.True(downloaded.IsSuccess, downloaded.ToString());
        Assert.NotEmpty(loggers.Entries);
        foreach (TestLogEntry entry in loggers.Entries)
        {
            Assert.DoesNotContain(marker, entry.Message, StringComparison.Ordinal);
            Assert.DoesNotContain(marker, entry.Exception?.ToString() ?? string.Empty, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task Operations_LogTheNameTargetElapsedTimeAndBytes_AtDebug()
    {
        await using FakeFtpServer server = FtpFixtures.Start();
        using var local = new TempDirectory();
        string source = local.File("up.txt");
        File.WriteAllText(source, "0123456789");
        using var loggers = new TestLogger();
        await using FtpClient client = FtpClient.Create(FtpTestClients.OptionsFor(server), loggers);

        Assert.True((await client.ListAsync("/docs")).IsSuccess);
        Assert.True((await client.UploadFileAsync(source, "/docs/up.txt")).IsSuccess);
        Assert.True((await client.DownloadFileAsync("/docs/recipe.txt", local.File("copy.txt"))).IsSuccess);
        Assert.True((await client.UploadAsync(new MemoryStream(new byte[7]), "/docs/seven.bin")).IsSuccess);

        string[] debug = loggers.GetEntries(LogLevel.Debug).Select(e => e.Message).ToArray();
        Assert.Contains(debug, m => m.StartsWith("FTP List /docs succeeded in ", StringComparison.Ordinal) && m.EndsWith(" ms.", StringComparison.Ordinal));
        Assert.Contains(debug, m => m.StartsWith("FTP UploadFile /docs/up.txt succeeded in ", StringComparison.Ordinal) && m.EndsWith(" ms, 10 bytes.", StringComparison.Ordinal));
        Assert.Contains(debug, m => m.StartsWith("FTP DownloadFile /docs/recipe.txt succeeded in ", StringComparison.Ordinal) && m.EndsWith(" ms, 12 bytes.", StringComparison.Ordinal));
        Assert.Contains(debug, m => m.StartsWith("FTP Upload /docs/seven.bin succeeded in ", StringComparison.Ordinal) && m.EndsWith(" ms, 7 bytes.", StringComparison.Ordinal));
        Assert.Empty(loggers.GetEntries(LogLevel.Warning));
    }

    [Fact]
    public async Task FailedOperation_LogsAWarning_WithTheKindAndTheReplyCode()
    {
        await using FakeFtpServer server = FtpFixtures.Start();
        using var loggers = new TestLogger();
        await using FtpClient client = FtpClient.Create(FtpTestClients.OptionsFor(server), loggers);

        CommResult result = await client.DeleteFileAsync("/docs/none.txt");

        Assert.Equal(CommErrorKind.RemoteError, result.ErrorKind);
        TestLogEntry warning = Assert.Single(loggers.GetEntries(LogLevel.Warning), e => e.CategoryName != "Junevy.Communication.Ftp.Sdk");
        Assert.StartsWith("FTP DeleteFile /docs/none.txt failed after ", warning.Message, StringComparison.Ordinal);
        Assert.Contains("RemoteError (reply 550)", warning.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CancelledOperation_IsLoggedAtDebug_NotAsAWarning()
    {
        await using FakeFtpServer server = FtpFixtures.Start();
        using var loggers = new TestLogger();
        await using FtpClient client = FtpClient.Create(FtpTestClients.OptionsFor(server), loggers);

        CommResult<IReadOnlyList<FtpEntry>> result = await client.ListAsync("/docs", new CancellationToken(canceled: true));

        Assert.Equal(CommErrorKind.Cancelled, result.ErrorKind);
        Assert.Contains(loggers.GetEntries(LogLevel.Debug), e => e.Message.StartsWith("FTP List /docs was cancelled after ", StringComparison.Ordinal));
        Assert.Empty(loggers.GetEntries(LogLevel.Warning));
    }

    [Fact]
    public void Create_WithAllowUntrustedCertificates_LogsAWarning()
    {
        using var loggers = new TestLogger();

        using FtpClient client = FtpClient.Create(
            new FtpClientOptions { Host = "ftp.local", AllowUntrustedCertificates = true },
            loggers);

        Assert.Contains(loggers.GetEntries(LogLevel.Warning), e => e.Message.Contains("AllowUntrustedCertificates", StringComparison.Ordinal));
    }
}
