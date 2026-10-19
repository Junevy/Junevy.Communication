using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Junevy.Communication.Channels;
using Junevy.Communication.Core.Results;
using Junevy.Communication.DebugAssistant.Common;
using Junevy.Communication.DebugAssistant.Protocols.Channels.Editors;
using Junevy.Communication.DebugAssistant.Protocols.Channels.Settings;
using Junevy.Communication.DebugAssistant.Sessions;
using Junevy.Communication.DebugAssistant.Shell;
using Junevy.Communication.DebugAssistant.Workspace;
using Junevy.Communication.Tcp;
using Microsoft.Extensions.Logging;

namespace Junevy.Communication.DebugAssistant.Protocols.Channels;

/// <summary>
/// TCP 服务端页（计划第 7.13 节）：启动与停止监听，会话列表，向选中的会话发送、广播、关闭会话，自动应答规则。
/// 每次启动都创建新的库服务端（DA-D9），因此停止后可以再次启动，并使用新的参数。
/// </summary>
public sealed partial class TcpServerSessionViewModel : SessionViewModel
{
    private const int MaxReceivedFrames = 500;
    private const int MaxSendHistory = 50;

    private readonly UiTicker _refresh;
    private TcpServer? _server;

    /// <summary>自动应答规则的编译结果。界面线程替换整个数组，库的事件线程只读取。</summary>
    private volatile CompiledAutoReply[] _rules = [];

    public TcpServerSessionViewModel(ProtocolDescriptor descriptor, SessionServices dependencies)
        : base(descriptor, dependencies)
    {
        Settings = new TcpServerEditor();
        AutoReplies.CollectionChanged += (_, _) => _rules = CompileRules(AutoReplies);
        SendToSelectedCommand = new AsyncRelayCommand(SendToSelectedAsync, () => SelectedSession is not null);
        BroadcastCommand = new AsyncRelayCommand(BroadcastAsync);
        CloseSelectedCommand = new AsyncRelayCommand(CloseSelectedAsync, () => SelectedSession is not null);
        AddAutoReplyCommand = new RelayCommand(AddAutoReply);
        RemoveAutoReplyCommand = new RelayCommand<AutoReplyRule?>(RemoveAutoReply);
        _refresh = new UiTicker(TimeSpan.FromSeconds(1), RefreshSessions, dependencies.Dispatcher);
    }

    /// <summary>监听、会话、每个会话的握手与帧的全部参数。</summary>
    public TcpServerEditor Settings { get; }

    /// <summary>当前会话，最早连接的在前。</summary>
    public ObservableCollection<ServerSession> Sessions { get; } = [];

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SendToSelectedCommand))]
    [NotifyCanExecuteChangedFor(nameof(CloseSelectedCommand))]
    private ServerSession? _selectedSession;

    /// <summary>收到的帧，最新的在最前（来源为会话编号与远端地址）。</summary>
    public ObservableCollection<ReceivedFrame> ReceivedFrames { get; } = [];

    /// <summary>成功发送过的内容，最新的在最前。</summary>
    public ObservableCollection<string> SendHistory { get; } = [];

    /// <summary>自动应答规则，按顺序检查。</summary>
    public ObservableCollection<AutoReplyRule> AutoReplies { get; } = [];

    [ObservableProperty]
    private string _autoReplyMatch = string.Empty;

    [ObservableProperty]
    private string _autoReplyReply = "hex:";

    [ObservableProperty]
    private bool _autoReplyEcho = true;

    /// <summary>要发送的内容（<c>hex:</c> 或文本语法）。</summary>
    [ObservableProperty]
    private string _payloadText = "hex:";

    [ObservableProperty]
    private EncodingChoice _payloadEncoding = EncodingChoice.Utf8;

    [ObservableProperty]
    private ChecksumKind _checksum = ChecksumKind.None;

    /// <summary>连接栏的统计文字（每秒刷新）。</summary>
    [ObservableProperty]
    private string _statisticsText = "未运行";

    public IAsyncRelayCommand SendToSelectedCommand { get; }

    public IAsyncRelayCommand BroadcastCommand { get; }

    public IAsyncRelayCommand CloseSelectedCommand { get; }

    public IRelayCommand AddAutoReplyCommand { get; }

    public IRelayCommand<AutoReplyRule?> RemoveAutoReplyCommand { get; }

    public override string ConnectText => "启动";

    public override string DisconnectText => "停止";

    /// <summary>操作记录里显示的监听地址。</summary>
    private string Endpoint => $"{Settings.ListenAddress}:{Settings.Port}";

    public override SessionSettings CaptureSettings() => new TcpServerSettings(Settings.Capture(), AutoReplies.ToArray());

    public override void ApplySettings(SessionSettings settings)
    {
        if (settings is not TcpServerSettings saved)
        {
            throw new ArgumentException("The settings do not belong to a TCP server page.", nameof(settings));
        }

        try
        {
            _ = CompileRules(saved.AutoReplies);
        }
        catch (FormatException ex)
        {
            throw new ArgumentException(ex.Message, nameof(settings));
        }

        Settings.Apply(saved.Values);
        AutoReplies.Clear();
        foreach (AutoReplyRule rule in saved.AutoReplies)
        {
            AutoReplies.Add(rule);
        }
    }

    protected override async Task ConnectCoreAsync(CancellationToken cancellationToken)
    {
        if (!Settings.IsValid)
        {
            throw new CommFailureException(CommResult.Fail("参数有误，请先修正标红的字段。", CommErrorKind.InvalidRequest));
        }

        TcpServer server = CreateServerOrFail();
        server.SessionConnected += (_, e) =>
        {
            ITcpSession session = e.Session;
            PostToUi(() => AddSession(server, session));
        };
        server.SessionClosed += (_, e) =>
        {
            ITcpSession session = e.Session;
            DisconnectReason reason = e.Reason;
            PostToUi(() => RemoveSession(server, session, reason));
        };
        server.FrameReceived += (_, e) => OnFrameReceived(server, e);
        server.StateChanged += (_, e) =>
        {
            ServerState state = e.CurrentState;
            PostToUi(() => OnServerStateChanged(server, state));
        };

        State = SessionState.Connecting;
        try
        {
            CommResult started = await server.StartAsync(cancellationToken);
            if (!started.IsSuccess)
            {
                throw new CommFailureException(started);
            }
        }
        catch
        {
            State = SessionState.NotConnected;
            await server.DisposeAsync();
            throw;
        }

        _server = server;
        State = SessionState.Running;
    }

    protected override async Task DisconnectCoreAsync(CancellationToken cancellationToken)
    {
        // 没有在运行时什么也不做：释放页签时不应改动界面状态。
        if (_server is not { } server)
        {
            return;
        }

        // 先改状态：库在停止时发出的 StateChanged 会排在后面，此时已不是"运行中"，不会被当作故障。
        State = SessionState.Stopped;
        _server = null;
        await server.StopAsync(cancellationToken);
        await server.DisposeAsync();
        Sessions.Clear();
        SelectedSession = null;
    }

    public override async ValueTask DisposeAsync()
    {
        try
        {
            await base.DisposeAsync();
        }
        finally
        {
            await _refresh.DisposeAsync();
        }
    }

    /// <summary>服务端自行停止（不是用户请求停止）时页签进入故障状态。</summary>
    private void OnServerStateChanged(TcpServer owner, ServerState state)
    {
        if (ReferenceEquals(owner, _server) && state == ServerState.Stopped && State == SessionState.Running)
        {
            State = SessionState.Faulted;
        }
    }

    private void AddSession(TcpServer owner, ITcpSession session)
    {
        if (!ReferenceEquals(owner, _server))
        {
            return;
        }

        var row = new ServerSession(session);
        Sessions.Add(row);
        RecordOperation(new OperationRecord(Dependencies.Clock.GetLocalNow(), "会话连接", row.Source, TimeSpan.Zero, true, CommErrorKind.None, null, string.Empty));
    }

    private void RemoveSession(TcpServer owner, ITcpSession session, DisconnectReason reason)
    {
        if (!ReferenceEquals(owner, _server) || Sessions.FirstOrDefault(row => row.Id == session.Id) is not { } row)
        {
            return;
        }

        if (ReferenceEquals(SelectedSession, row))
        {
            SelectedSession = null;
        }

        Sessions.Remove(row);
        RecordOperation(new OperationRecord(Dependencies.Clock.GetLocalNow(), "会话断开", row.Source, TimeSpan.Zero, true, CommErrorKind.None, null, reason.ToString()));
    }

    /// <summary>收到的帧：先检查自动应答（在库的事件线程上发送，不等待界面），再交给界面显示。</summary>
    private void OnFrameReceived(TcpServer owner, TcpSessionFrameEventArgs e)
    {
        foreach (CompiledAutoReply rule in _rules)
        {
            if (rule.TryReply(e.Data, out byte[] reply))
            {
                _ = AutoReplyAsync(e.Session, reply);
                break;
            }
        }

        string source = SourceOf(e.Session);
        DateTimeOffset receivedAt = e.ReceivedAt;
        byte[] data = e.Data;
        PostToUi(() =>
        {
            if (ReferenceEquals(owner, _server))
            {
                ReceivedFrames.InsertNewest(new ReceivedFrame(receivedAt, data, source), MaxReceivedFrames);
            }
        });
    }

    /// <summary>自动应答发送失败时记入操作记录；成功不记录，避免刷屏。异常不逃出（计划 U7）。</summary>
    private async Task AutoReplyAsync(ITcpSession session, byte[] reply)
    {
        DateTimeOffset started = Dependencies.Clock.GetLocalNow();
        string source = SourceOf(session);
        try
        {
            CommResult result = await session.SendAsync(reply);
            if (!result.IsSuccess)
            {
                PostToUi(() => RecordOperation(OperationRecords.From(started, "自动应答", source, TimeSpan.Zero, result)));
            }
        }
        catch (Exception ex)
        {
            PostToUi(() => RecordOperation(new OperationRecord(started, "自动应答", source, TimeSpan.Zero, false, CommErrorKind.Unspecified, null, ex.Message)));
        }
    }

    private async Task SendToSelectedAsync()
    {
        if (SelectedSession is not { } target || !TryPreparePayload(out byte[] payload))
        {
            return;
        }

        DateTimeOffset started = Dependencies.Clock.GetLocalNow();
        CommResult result = await target.Session.SendAsync(payload, CancellationToken.None);
        RecordOperation(OperationRecords.From(started, "发送", target.Source, TimeSpan.Zero, result));
        if (result.IsSuccess)
        {
            SendHistory.InsertNewest(PayloadText, MaxSendHistory);
        }
    }

    private async Task BroadcastAsync()
    {
        if (_server is not { } server)
        {
            RecordOperation(new OperationRecord(Dependencies.Clock.GetLocalNow(), "广播", Endpoint, TimeSpan.Zero, false, CommErrorKind.NotConnected, null, "服务端没有在运行。"));
            return;
        }

        if (!TryPreparePayload(out byte[] payload))
        {
            return;
        }

        DateTimeOffset started = Dependencies.Clock.GetLocalNow();
        int sent = await server.BroadcastAsync(payload, cancellationToken: CancellationToken.None);
        RecordOperation(new OperationRecord(started, "广播", Endpoint, TimeSpan.Zero, true, CommErrorKind.None, null, $"已发送到 {sent} 个会话。"));
        if (sent > 0)
        {
            SendHistory.InsertNewest(PayloadText, MaxSendHistory);
        }
    }

    private async Task CloseSelectedAsync()
    {
        if (SelectedSession is not { } target)
        {
            return;
        }

        DateTimeOffset started = Dependencies.Clock.GetLocalNow();
        await target.Session.CloseAsync(CancellationToken.None);
        RecordOperation(new OperationRecord(started, "关闭会话", target.Source, TimeSpan.Zero, true, CommErrorKind.None, null, "已请求关闭该会话。"));
    }

    private void AddAutoReply()
    {
        var rule = new AutoReplyRule(AutoReplyMatch, AutoReplyReply, AutoReplyEcho);
        try
        {
            _ = rule.Compile();
        }
        catch (FormatException ex)
        {
            RecordOperation(new OperationRecord(Dependencies.Clock.GetLocalNow(), "添加自动应答", Endpoint, TimeSpan.Zero, false, CommErrorKind.InvalidRequest, null, ex.Message));
            return;
        }

        AutoReplies.Add(rule);
    }

    private void RemoveAutoReply(AutoReplyRule? rule)
    {
        if (rule is not null)
        {
            AutoReplies.Remove(rule);
        }
    }

    /// <summary>每秒刷新会话的统计与连接栏的文字（界面线程）。</summary>
    private void RefreshSessions()
    {
        foreach (ServerSession row in Sessions)
        {
            row.Refresh();
        }

        StatisticsText = State == SessionState.Running
            ? $"会话 {Sessions.Count} 个（最大 {(Settings.MaxSessions == 0 ? "不限" : Settings.MaxSessions.ToString())}）"
            : StateText;
    }

    /// <summary>准备要发送的字节：先按编码解析，再追加校验（DA-D12）。格式错误时记入操作记录。</summary>
    private bool TryPreparePayload(out byte[] payload)
    {
        try
        {
            payload = Checksums.Append(Checksum, ByteInput.Parse(PayloadText, PayloadEncoding));
            return true;
        }
        catch (FormatException ex)
        {
            RecordOperation(new OperationRecord(Dependencies.Clock.GetLocalNow(), "发送", Endpoint, TimeSpan.Zero, false, CommErrorKind.InvalidRequest, null, ex.Message));
            payload = [];
            return false;
        }
    }

    private TcpServer CreateServerOrFail()
    {
        try
        {
            return new TcpServer(Settings.ToConfig(), new Logger<TcpServer>(SessionLoggerFactory), Settings.ToComponents());
        }
        catch (ArgumentException ex)
        {
            throw new CommFailureException(CommResult.Fail(ex.Message, CommErrorKind.InvalidRequest));
        }
    }

    private static CompiledAutoReply[] CompileRules(IEnumerable<AutoReplyRule> rules) =>
        rules.Select(rule => rule.Compile()).ToArray();

    private static string SourceOf(ITcpSession session) => $"#{session.Id} {session.RemoteEndPoint}";
}

/// <summary>服务端会话列表的一行（计划第 7.13 节：编号、远端、连接时间、TLS、统计）。</summary>
public sealed partial class ServerSession : ObservableObject
{
    public ServerSession(ITcpSession session)
    {
        Session = session;
        Id = session.Id;
        Remote = session.RemoteEndPoint.ToString();
        ConnectedAt = session.ConnectedAt;
        IsTls = session.IsTlsActive;
        Refresh();
    }

    /// <summary>库的会话，用于发送与关闭。</summary>
    public ITcpSession Session { get; }

    public long Id { get; }

    public string Remote { get; }

    public DateTimeOffset ConnectedAt { get; }

    public bool IsTls { get; }

    /// <summary>收到的帧与操作记录里显示的来源。</summary>
    public string Source => $"#{Id} {Remote}";

    [ObservableProperty]
    private string _statistics = string.Empty;

    /// <summary>刷新统计文字（界面线程）。</summary>
    public void Refresh()
    {
        ConnectionStatistics stats = Session.Statistics;
        Statistics = $"发送 {stats.FramesSent} 帧 / 接收 {stats.FramesReceived} 帧";
    }
}
