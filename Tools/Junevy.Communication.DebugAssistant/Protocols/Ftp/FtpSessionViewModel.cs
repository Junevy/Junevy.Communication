using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Junevy.Communication.Core.Results;
using Junevy.Communication.DebugAssistant.Sessions;
using Junevy.Communication.DebugAssistant.Shell;
using Junevy.Communication.DebugAssistant.Workspace;
using Junevy.Communication.Ftp;

namespace Junevy.Communication.DebugAssistant.Protocols.Ftp;

/// <summary>
/// FTP/FTPS 页（计划第 7.12 节）：连接选项（加密方式、证书策略、文件名编码、超时）；远程目录浏览（进入、上级、刷新）；
/// 上传与下载（进度、Overwrite 或 Skip）；新建目录（逐级创建）、删除文件、递归删除目录（先确认）、重命名、检查存在与大小。
/// 连接只检查地址与凭据；之后的操作由库按需连接，同一个客户端的操作排队执行。
/// </summary>
public sealed partial class FtpSessionViewModel : SessionViewModel
{
    private const string NotConnectedMessage = "页签没有连接，请先连接。";

    private FtpClient? _client;

    public FtpSessionViewModel(ProtocolDescriptor descriptor, SessionServices dependencies)
        : base(descriptor, dependencies)
    {
        RefreshCommand = new AsyncRelayCommand(RefreshAsync, () => IsLinked);
        GoUpCommand = new AsyncRelayCommand(GoUpAsync, () => IsLinked);
        EnterDirectoryCommand = new AsyncRelayCommand<FtpEntryRow?>(EnterDirectoryAsync);
        UploadCommand = new AsyncRelayCommand(UploadAsync, () => IsLinked);
        DownloadCommand = new AsyncRelayCommand(DownloadAsync, () => IsLinked);
        CreateDirectoryCommand = new AsyncRelayCommand(CreateDirectoryAsync, () => IsLinked);
        DeleteFileCommand = new AsyncRelayCommand(DeleteFileAsync, () => IsLinked);
        DeleteDirectoryCommand = new AsyncRelayCommand(DeleteDirectoryAsync, () => IsLinked);
        RenameCommand = new AsyncRelayCommand(RenameAsync, () => IsLinked);
        CheckSelectedCommand = new AsyncRelayCommand(CheckSelectedAsync, () => IsLinked);
        PropertyChanged += OnOwnPropertyChanged;
    }

    public IReadOnlyList<FtpEncryption> EncryptionChoices { get; } = Enum.GetValues<FtpEncryption>();

    public IReadOnlyList<FtpExistsMode> ExistsModeChoices { get; } = Enum.GetValues<FtpExistsMode>();

    /// <summary>远程目录列表，目录排在前面。</summary>
    public ObservableCollection<FtpEntryRow> Entries { get; } = [];

    /// <summary>已连接（本页的操作需要客户端存在）。</summary>
    public bool IsLinked => State == SessionState.Connected;

    [ObservableProperty]
    private string _host = "127.0.0.1";

    [ObservableProperty]
    private int _port = 21;

    [ObservableProperty]
    private string _userName = string.Empty;

    /// <summary>密码只保存在内存中，不写入工作区。</summary>
    [ObservableProperty]
    private string _password = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsImplicit))]
    private FtpEncryption _encryption = FtpEncryption.None;

    /// <summary>隐式 FTPS 通常使用端口 990；选择它时只提示，不会自动修改端口。</summary>
    public bool IsImplicit => Encryption == FtpEncryption.Implicit;

    [ObservableProperty]
    private bool _allowUntrustedCertificates;

    /// <summary>文件名与列表的编码，例如 GBK；留空使用库的默认值。</summary>
    [ObservableProperty]
    private string _encodingName = string.Empty;

    [ObservableProperty]
    private int _connectTimeoutMs = 10000;

    [ObservableProperty]
    private int _readTimeoutMs = 15000;

    [ObservableProperty]
    private int _dataConnectionTimeoutMs = 15000;

    /// <summary>库的连接状态：已连接，或未连接（下次操作时按需连接）。</summary>
    [ObservableProperty]
    private string _linkStateText = "未连接";

    [ObservableProperty]
    private string _currentDirectory = "/";

    [ObservableProperty]
    private FtpEntryRow? _selectedEntry;

    [ObservableProperty]
    private string _newDirectoryName = string.Empty;

    [ObservableProperty]
    private string _renameTo = string.Empty;

    [ObservableProperty]
    private FtpExistsMode _existsMode = FtpExistsMode.Overwrite;

    /// <summary>传输进度的数值（0 至 100）；总长度未知时为不定进度。</summary>
    [ObservableProperty]
    private double _progressValue;

    [ObservableProperty]
    private bool _progressIndeterminate;

    [ObservableProperty]
    private string _progressText = string.Empty;

    /// <summary>操作的说明：结果、失败原因或需要的操作。</summary>
    [ObservableProperty]
    private string _operationMessage = string.Empty;

    public IAsyncRelayCommand RefreshCommand { get; }

    public IAsyncRelayCommand GoUpCommand { get; }

    public IAsyncRelayCommand<FtpEntryRow?> EnterDirectoryCommand { get; }

    public IAsyncRelayCommand UploadCommand { get; }

    public IAsyncRelayCommand DownloadCommand { get; }

    public IAsyncRelayCommand CreateDirectoryCommand { get; }

    public IAsyncRelayCommand DeleteFileCommand { get; }

    public IAsyncRelayCommand DeleteDirectoryCommand { get; }

    public IAsyncRelayCommand RenameCommand { get; }

    public IAsyncRelayCommand CheckSelectedCommand { get; }

    public override SessionSettings CaptureSettings() => Capture();

    public override void ApplySettings(SessionSettings settings)
    {
        if (settings is not FtpSettings saved)
        {
            throw new ArgumentException("The settings do not belong to an FTP page.", nameof(settings));
        }

        Host = saved.Host;
        Port = saved.Port;
        UserName = saved.UserName ?? string.Empty;
        Encryption = saved.Encryption;
        AllowUntrustedCertificates = saved.AllowUntrustedCertificates;
        EncodingName = saved.EncodingName ?? string.Empty;
        ConnectTimeoutMs = saved.ConnectTimeout;
        ReadTimeoutMs = saved.ReadTimeout;
        DataConnectionTimeoutMs = saved.DataConnectionTimeout;
    }

    protected override async Task ConnectCoreAsync(CancellationToken cancellationToken)
    {
        FtpClient client = CreateClient(Capture());
        CommResult connected = await client.ConnectAsync(cancellationToken).ConfigureAwait(true);
        if (!connected.IsSuccess)
        {
            await client.DisposeAsync().ConfigureAwait(true);
            throw new CommFailureException(connected);
        }

        _client = client;
        State = SessionState.Connected;
        RefreshLinkState();
        await BrowseAsync(client, CurrentDirectory).ConfigureAwait(true);
    }

    protected override async Task DisconnectCoreAsync(CancellationToken cancellationToken)
    {
        if (_client is not { } client)
        {
            ResetState();
            return;
        }

        _client = null;
        Entries.Clear();
        ResetState();
        try
        {
            await client.DisconnectAsync(cancellationToken).ConfigureAwait(true);
        }
        finally
        {
            await client.DisposeAsync().ConfigureAwait(true);
        }
    }

    private FtpSettings Capture() => new(
        Host.Trim(),
        Port,
        FtpSupport.Optional(UserName),
        Encryption,
        AllowUntrustedCertificates,
        FtpSupport.Optional(EncodingName),
        ConnectTimeoutMs,
        ReadTimeoutMs,
        DataConnectionTimeoutMs);

    // 库拒绝选项（例如编码名称未知）时抛出 ArgumentException，这里转成请求无效，与其它页签一致。
    private FtpClient CreateClient(FtpSettings settings)
    {
        if (settings.Host.Length == 0)
        {
            throw new CommFailureException(CommResult.Fail("主机地址不能为空。", CommErrorKind.InvalidRequest));
        }

        var options = new FtpClientOptions
        {
            Host = settings.Host,
            Port = settings.Port,
            UserName = settings.UserName,
            Password = Password.Length == 0 ? null : Password,
            Encryption = settings.Encryption,
            AllowUntrustedCertificates = settings.AllowUntrustedCertificates,
            EncodingName = settings.EncodingName,
            ConnectTimeout = settings.ConnectTimeout,
            ReadTimeout = settings.ReadTimeout,
            DataConnectionTimeout = settings.DataConnectionTimeout,
        };

        try
        {
            return FtpClient.Create(options, SessionLoggerFactory);
        }
        catch (ArgumentException ex)
        {
            throw new CommFailureException(CommResult.Fail(ex.Message, CommErrorKind.InvalidRequest));
        }
    }

    private async Task RefreshAsync()
    {
        if (_client is not { } client)
        {
            OperationMessage = NotConnectedMessage;
            return;
        }

        await BrowseAsync(client, CurrentDirectory).ConfigureAwait(true);
    }

    private async Task GoUpAsync()
    {
        if (_client is not { } client)
        {
            OperationMessage = NotConnectedMessage;
            return;
        }

        await BrowseAsync(client, FtpSupport.Parent(CurrentDirectory)).ConfigureAwait(true);
    }

    private async Task EnterDirectoryAsync(FtpEntryRow? row)
    {
        if (_client is not { } client)
        {
            OperationMessage = NotConnectedMessage;
            return;
        }

        if (row is not { IsDirectory: true })
        {
            OperationMessage = "只能进入目录。";
            return;
        }

        await BrowseAsync(client, row.FullName).ConfigureAwait(true);
    }

    // 刷新目录列表：成功时替换列表并返回 true，不写操作说明（写操作刷新后仍显示自己的结果）；失败时保留原来的列表，写明原因并返回 false。
    private async Task<bool> ListCurrentDirectoryAsync(FtpClient client)
    {
        DateTimeOffset started = Dependencies.Clock.GetLocalNow();
        Stopwatch clock = Stopwatch.StartNew();
        CommResult<IReadOnlyList<FtpEntry>> result = await client.ListAsync(CurrentDirectory).ConfigureAwait(true);
        RecordOperation(OperationRecords.From(started, "列目录", CurrentDirectory, clock.Elapsed, result));
        RefreshLinkState();
        if (!ReferenceEquals(_client, client))
        {
            return false;
        }

        if (!result.IsSuccess || result.Data is null)
        {
            OperationMessage = ResultText.Failure(result.ErrorKind, result.ErrorMessage);
            return false;
        }

        Entries.Clear();
        foreach (FtpEntry entry in result.Data
            .OrderBy(entry => entry.Type != FtpEntryType.Directory)
            .ThenBy(entry => entry.Name, StringComparer.OrdinalIgnoreCase))
        {
            Entries.Add(FtpSupport.ToRow(entry));
        }

        SelectedEntry = null;
        return true;
    }

    // 浏览到指定目录（连接、刷新、上级、进入）：成功时说明目录概况；列出失败时回到原来的目录，使路径与列表一致。
    private async Task BrowseAsync(FtpClient client, string directory)
    {
        string previous = CurrentDirectory;
        CurrentDirectory = directory;
        if (await ListCurrentDirectoryAsync(client).ConfigureAwait(true))
        {
            OperationMessage = $"{CurrentDirectory} 中有 {Entries.Count} 项。";
            return;
        }

        CurrentDirectory = previous;
    }

    private async Task UploadAsync()
    {
        if (_client is not { } client)
        {
            OperationMessage = NotConnectedMessage;
            return;
        }

        string? localPath = Dependencies.Files.PickOpenFile("选择要上传的文件", "所有文件|*.*");
        if (localPath is null)
        {
            return;
        }

        string remotePath = FtpSupport.Combine(CurrentDirectory, Path.GetFileName(localPath));
        ResetProgress();
        var progress = new Progress<FtpTransferProgress>(OnProgress);
        DateTimeOffset started = Dependencies.Clock.GetLocalNow();
        Stopwatch clock = Stopwatch.StartNew();
        CommResult<FtpTransferStatus> result = await client.UploadFileAsync(localPath, remotePath, ExistsMode, progress).ConfigureAwait(true);
        RecordOperation(OperationRecords.From(started, "上传", remotePath, clock.Elapsed, result));
        OperationMessage = result.IsSuccess
            ? FtpSupport.TransferText(result.Data, remotePath) + UploadNote(result.Data)
            : ResultText.Failure(result.ErrorKind, result.ErrorMessage);
        await ListCurrentDirectoryAsync(client).ConfigureAwait(true);
    }

    private async Task DownloadAsync()
    {
        if (_client is not { } client)
        {
            OperationMessage = NotConnectedMessage;
            return;
        }

        if (SelectedEntry is not { IsDirectory: false } entry)
        {
            OperationMessage = "请先选择一个文件。";
            return;
        }

        string? localPath = Dependencies.Files.PickSaveFile("保存为", entry.Name, "所有文件|*.*");
        if (localPath is null)
        {
            return;
        }

        ResetProgress();
        var progress = new Progress<FtpTransferProgress>(OnProgress);
        DateTimeOffset started = Dependencies.Clock.GetLocalNow();
        Stopwatch clock = Stopwatch.StartNew();
        CommResult<FtpTransferStatus> result = await client.DownloadFileAsync(entry.FullName, localPath, ExistsMode, progress).ConfigureAwait(true);
        RecordOperation(OperationRecords.From(started, "下载", entry.FullName, clock.Elapsed, result));
        OperationMessage = result.IsSuccess
            ? FtpSupport.TransferText(result.Data, localPath) + DownloadNote(result.Data)
            : ResultText.Failure(result.ErrorKind, result.ErrorMessage);
    }

    private async Task CreateDirectoryAsync()
    {
        if (_client is not { } client)
        {
            OperationMessage = NotConnectedMessage;
            return;
        }

        string name = NewDirectoryName.Trim();
        if (name.Length == 0)
        {
            OperationMessage = "目录名称不能为空。";
            return;
        }

        string remotePath = FtpSupport.Combine(CurrentDirectory, name);
        DateTimeOffset started = Dependencies.Clock.GetLocalNow();
        Stopwatch clock = Stopwatch.StartNew();
        CommResult result = await client.CreateDirectoryAsync(remotePath).ConfigureAwait(true);
        RecordOperation(OperationRecords.From(started, "新建目录", remotePath, clock.Elapsed, result));
        OperationMessage = result.IsSuccess ? $"已新建目录 {remotePath}。" : ResultText.Failure(result.ErrorKind, result.ErrorMessage);
        if (result.IsSuccess)
        {
            NewDirectoryName = string.Empty;
            await ListCurrentDirectoryAsync(client).ConfigureAwait(true);
        }
    }

    private async Task DeleteFileAsync()
    {
        if (_client is not { } client)
        {
            OperationMessage = NotConnectedMessage;
            return;
        }

        if (SelectedEntry is not { IsDirectory: false } entry)
        {
            OperationMessage = "请先选择一个文件。";
            return;
        }

        DateTimeOffset started = Dependencies.Clock.GetLocalNow();
        Stopwatch clock = Stopwatch.StartNew();
        CommResult result = await client.DeleteFileAsync(entry.FullName).ConfigureAwait(true);
        RecordOperation(OperationRecords.From(started, "删除文件", entry.FullName, clock.Elapsed, result));
        OperationMessage = result.IsSuccess ? $"已删除 {entry.FullName}。" : ResultText.Failure(result.ErrorKind, result.ErrorMessage);
        if (result.IsSuccess)
        {
            await ListCurrentDirectoryAsync(client).ConfigureAwait(true);
        }
    }

    // 递归删除会删掉目录中的全部内容，因此先确认；用户取消时不发送任何命令。
    private async Task DeleteDirectoryAsync()
    {
        if (_client is not { } client)
        {
            OperationMessage = NotConnectedMessage;
            return;
        }

        if (SelectedEntry is not { IsDirectory: true } entry)
        {
            OperationMessage = "请先选择一个目录。";
            return;
        }

        bool confirmed = await Dependencies.Confirm
            .ConfirmAsync("递归删除目录", $"将删除 {entry.FullName} 及其中的所有文件和子目录，不能恢复。是否继续？")
            .ConfigureAwait(true);
        if (!confirmed)
        {
            OperationMessage = "已取消删除。";
            return;
        }

        DateTimeOffset started = Dependencies.Clock.GetLocalNow();
        Stopwatch clock = Stopwatch.StartNew();
        CommResult result = await client.DeleteDirectoryAsync(entry.FullName).ConfigureAwait(true);
        RecordOperation(OperationRecords.From(started, "递归删除目录", entry.FullName, clock.Elapsed, result));
        OperationMessage = result.IsSuccess ? $"已删除目录 {entry.FullName}。" : ResultText.Failure(result.ErrorKind, result.ErrorMessage);
        if (result.IsSuccess)
        {
            await ListCurrentDirectoryAsync(client).ConfigureAwait(true);
        }
    }

    private async Task RenameAsync()
    {
        if (_client is not { } client)
        {
            OperationMessage = NotConnectedMessage;
            return;
        }

        if (SelectedEntry is not { } entry)
        {
            OperationMessage = "请先选择一个文件或目录。";
            return;
        }

        string newName = RenameTo.Trim();
        if (newName.Length == 0)
        {
            OperationMessage = "请填写新名称。";
            return;
        }

        string target = FtpSupport.Combine(CurrentDirectory, newName);
        DateTimeOffset started = Dependencies.Clock.GetLocalNow();
        Stopwatch clock = Stopwatch.StartNew();
        CommResult result = await client.RenameAsync(entry.FullName, target).ConfigureAwait(true);
        RecordOperation(OperationRecords.From(started, "重命名", entry.FullName, clock.Elapsed, result));
        OperationMessage = result.IsSuccess ? $"已重命名为 {target}。" : ResultText.Failure(result.ErrorKind, result.ErrorMessage);
        if (result.IsSuccess)
        {
            RenameTo = string.Empty;
            await ListCurrentDirectoryAsync(client).ConfigureAwait(true);
        }
    }

    private async Task CheckSelectedAsync()
    {
        if (_client is not { } client)
        {
            OperationMessage = NotConnectedMessage;
            return;
        }

        if (SelectedEntry is not { } entry)
        {
            OperationMessage = "请先选择一个文件或目录。";
            return;
        }

        DateTimeOffset started = Dependencies.Clock.GetLocalNow();
        Stopwatch clock = Stopwatch.StartNew();
        if (entry.IsDirectory)
        {
            CommResult<bool> exists = await client.DirectoryExistsAsync(entry.FullName).ConfigureAwait(true);
            RecordOperation(OperationRecords.From(started, "检查目录", entry.FullName, clock.Elapsed, exists));
            OperationMessage = exists.IsSuccess
                ? $"目录{(exists.Data ? "存在" : "不存在")}：{entry.FullName}"
                : ResultText.Failure(exists.ErrorKind, exists.ErrorMessage);
            return;
        }

        CommResult<bool> fileExists = await client.FileExistsAsync(entry.FullName).ConfigureAwait(true);
        RecordOperation(OperationRecords.From(started, "检查文件", entry.FullName, clock.Elapsed, fileExists));
        if (!fileExists.IsSuccess)
        {
            OperationMessage = ResultText.Failure(fileExists.ErrorKind, fileExists.ErrorMessage);
            return;
        }

        if (!fileExists.Data)
        {
            OperationMessage = $"文件不存在：{entry.FullName}";
            return;
        }

        CommResult<long> size = await client.GetFileSizeAsync(entry.FullName).ConfigureAwait(true);
        RecordOperation(OperationRecords.From(started, "文件大小", entry.FullName, clock.Elapsed, size));
        OperationMessage = size.IsSuccess
            ? $"文件存在，大小 {size.Data} 字节：{entry.FullName}"
            : ResultText.Failure(size.ErrorKind, size.ErrorMessage);
    }

    // 只有真正传输了文件才写明原子性；跳过时没有发生传输，只写结果。
    private static string UploadNote(FtpTransferStatus status) =>
        status == FtpTransferStatus.Transferred ? "。上传不是原子操作：失败时远程文件可能不存在。" : "。";

    private static string DownloadNote(FtpTransferStatus status) =>
        status == FtpTransferStatus.Transferred ? "。下载是原子的：失败时本地原有文件保持不变。" : "。";

    private void OnProgress(FtpTransferProgress progress)
    {
        ProgressIndeterminate = progress.Percent is null;
        ProgressValue = progress.Percent ?? 0;
        ProgressText = FtpSupport.ProgressText(progress);
    }

    private void ResetProgress()
    {
        ProgressValue = 0;
        ProgressIndeterminate = true;
        ProgressText = string.Empty;
    }

    private void RefreshLinkState() =>
        LinkStateText = _client is { IsConnected: true } ? "已连接" : "未连接（下次操作时按需连接）";

    private void ResetState()
    {
        State = SessionState.NotConnected;
        LinkStateText = "未连接";
    }

    private void OnOwnPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(State))
        {
            return;
        }

        OnPropertyChanged(nameof(IsLinked));
        RefreshCommand.NotifyCanExecuteChanged();
        GoUpCommand.NotifyCanExecuteChanged();
        UploadCommand.NotifyCanExecuteChanged();
        DownloadCommand.NotifyCanExecuteChanged();
        CreateDirectoryCommand.NotifyCanExecuteChanged();
        DeleteFileCommand.NotifyCanExecuteChanged();
        DeleteDirectoryCommand.NotifyCanExecuteChanged();
        RenameCommand.NotifyCanExecuteChanged();
        CheckSelectedCommand.NotifyCanExecuteChanged();
    }
}
