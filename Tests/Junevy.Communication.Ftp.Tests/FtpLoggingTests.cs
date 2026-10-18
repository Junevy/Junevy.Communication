using Junevy.Communication.Core.Results;
using Junevy.Communication.Ftp.Tests.TestSupport;
using Junevy.Communication.Testing;
using Microsoft.Extensions.Logging;

namespace Junevy.Communication.Ftp.Tests;

/// <summary>
/// 日志（P63-D13、D15；Q40）：任何级别都不出现密码；SDK 的日志经桥接进入同一个日志工厂，类别为 <c>Junevy.Communication.Ftp.Sdk</c>。
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
    public void Create_WithAllowUntrustedCertificates_LogsAWarning()
    {
        using var loggers = new TestLogger();

        using FtpClient client = FtpClient.Create(
            new FtpClientOptions { Host = "ftp.local", AllowUntrustedCertificates = true },
            loggers);

        Assert.Contains(loggers.GetEntries(LogLevel.Warning), e => e.Message.Contains("AllowUntrustedCertificates", StringComparison.Ordinal));
    }
}
