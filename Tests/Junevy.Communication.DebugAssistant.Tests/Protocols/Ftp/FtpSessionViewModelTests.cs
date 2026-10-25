using System.Text.Json;
using Junevy.Communication.Core.Results;
using Junevy.Communication.DebugAssistant.Protocols.Ftp;
using Junevy.Communication.DebugAssistant.Sessions;
using Junevy.Communication.DebugAssistant.Shell;
using Junevy.Communication.DebugAssistant.Tests.Support;
using Junevy.Communication.Ftp;

namespace Junevy.Communication.DebugAssistant.Tests.Protocols.Ftp;

/// <summary>
/// FTP 页的视图模型，不连接服务器（计划 Task 19 第 2 条的单元部分）：默认值、设置往返（密码不保存）、主机为空与编码未知时拒绝、
/// 未连接时不能操作、隐式加密的提示标志。端到端的文件操作见 <c>FtpEndToEndTests</c>。
/// </summary>
public sealed class FtpSessionViewModelTests
{
    private static FtpSessionViewModel CreatePage(FakeConfirmService? confirm = null) =>
        new(ProtocolCatalog.Get(ProtocolKind.Ftp), FakeServices.Create(new ManualUiDispatcher(), confirm: confirm));

    private static string Json(object value) => JsonSerializer.Serialize(value);

    [Fact]
    public async Task Page_StartsNotConnected_OnTheRootDirectory()
    {
        await using var page = CreatePage();

        Assert.Equal((SessionState.NotConnected, false, "/", FtpExistsMode.Overwrite, 21), (page.State, page.IsLinked, page.CurrentDirectory, page.ExistsMode, page.Port));
        Assert.Equal((FtpEncryption.None, false), (page.Encryption, page.IsImplicit));
    }

    [Fact]
    public async Task ImplicitEncryption_ShowsThePortHint_WithoutChangingThePort()
    {
        await using var page = CreatePage();

        page.Encryption = FtpEncryption.Implicit;

        Assert.Equal((true, 21), (page.IsImplicit, page.Port));
    }

    [Fact]
    public async Task Settings_RoundTrip_AndThePasswordIsNotSaved()
    {
        await using var page = CreatePage();
        page.Host = "files.local";
        page.Port = 990;
        page.UserName = "line1";
        page.Password = "secret";
        page.Encryption = FtpEncryption.Implicit;
        page.AllowUntrustedCertificates = true;
        page.EncodingName = "GBK";
        page.ConnectTimeoutMs = 3000;
        page.ReadTimeoutMs = 4000;
        page.DataConnectionTimeoutMs = 5000;

        var saved = (FtpSettings)page.CaptureSettings();
        await using var other = CreatePage();
        other.ApplySettings(saved);

        Assert.Equal(Json(saved), Json(other.CaptureSettings()));
        Assert.Equal(string.Empty, other.Password);
    }

    [Fact]
    public async Task ConnectWithoutAHost_IsRefused_AndRecorded()
    {
        await using var page = CreatePage();
        page.Host = " ";

        await page.ConnectCommand.ExecuteAsync(null);

        Assert.Equal(SessionState.NotConnected, page.State);
        OperationRecord record = Assert.Single(page.Operations);
        Assert.Equal((false, CommErrorKind.InvalidRequest, "主机地址不能为空。"), (record.Succeeded, record.ErrorKind, record.Message));
    }

    [Fact]
    public async Task ConnectWithAnUnknownEncoding_IsRefused_BeforeAnyConnection()
    {
        await using var page = CreatePage();
        page.EncodingName = "no-such-encoding";

        await page.ConnectCommand.ExecuteAsync(null);

        Assert.Equal(SessionState.NotConnected, page.State);
        OperationRecord record = Assert.Single(page.Operations);
        Assert.Equal((false, CommErrorKind.InvalidRequest), (record.Succeeded, record.ErrorKind));
    }

    [Fact]
    public async Task Operations_AreUnavailable_UntilConnected()
    {
        await using var page = CreatePage();

        Assert.Equal(
            (false, false, false, false),
            (page.RefreshCommand.CanExecute(null), page.UploadCommand.CanExecute(null), page.DeleteDirectoryCommand.CanExecute(null), page.RenameCommand.CanExecute(null)));
    }

    [Fact]
    public async Task EnteringADirectory_WithoutAConnection_SaysToConnectFirst()
    {
        await using var page = CreatePage();

        await page.EnterDirectoryCommand.ExecuteAsync(null);

        Assert.Equal("页签没有连接，请先连接。", page.OperationMessage);
    }
}
