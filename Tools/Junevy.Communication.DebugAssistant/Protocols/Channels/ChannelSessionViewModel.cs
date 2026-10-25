using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Junevy.Communication.Channels;
using Junevy.Communication.Core.Diagnostics;
using Junevy.Communication.Core.Results;
using Junevy.Communication.DebugAssistant.Common;
using Junevy.Communication.DebugAssistant.Protocols.Channels.Editors;
using Junevy.Communication.DebugAssistant.Shell;
using Junevy.Communication.DebugAssistant.Sessions;

namespace Junevy.Communication.DebugAssistant.Protocols.Channels;

/// <summary>
/// 传输页（TCP 客户端、UDP、串口）的共同部分（计划第 7.13 节）：连接与断开、发送、请求与接收、定时发送、
/// 收到的帧、发送历史与统计。子类只负责创建库的通道、映射参数与传输特有的目标。
/// </summary>
public abstract partial class ChannelSessionViewModel : SessionViewModel
{
    private const int MaxReceivedFrames = 200;
    private const int MaxSendHistory = 50;

    private readonly UiTicker _statistics;
    private IClientChannel? _channel;
    private CancellationTokenSource? _periodicStop;

    protected ChannelSessionViewModel(ProtocolDescriptor descriptor, SessionServices dependencies, ChannelSettingsEditor settings)
        : base(descriptor, dependencies)
    {
        Settings = settings;
        _statistics = new UiTicker(TimeSpan.FromSeconds(1), RefreshStatistics, dependencies.Dispatcher);
        // 发送与请求可以同时在途：Keyed 关联的用途就是让多个请求交错，并由键把应答配对（计划第 1 节）。接收只有一个等待者，保持互斥。
        SendCommand = new AsyncRelayCommand(SendOnceAsync, AsyncRelayCommandOptions.AllowConcurrentExecutions);
        RequestCommand = new AsyncRelayCommand(RequestOnceAsync, AsyncRelayCommandOptions.AllowConcurrentExecutions);
        ReceiveCommand = new AsyncRelayCommand(ReceiveOnceAsync);
        StartPeriodicCommand = new RelayCommand(StartPeriodic, () => !IsPeriodicRunning);
        StopPeriodicCommand = new RelayCommand(StopPeriodic, () => IsPeriodicRunning);
    }

    /// <summary>通道参数编辑器（分帧、关联、心跳、重连、超时、套接字、TLS、握手）。</summary>
    public ChannelSettingsEditor Settings { get; }

    /// <summary>收到的帧，最新的在最前（来自 <c>FrameReceived</c> 与接收、请求的应答）。</summary>
    public ObservableCollection<ReceivedFrame> ReceivedFrames { get; } = [];

    /// <summary>成功发送过的内容，最新的在最前。</summary>
    public ObservableCollection<string> SendHistory { get; } = [];

    /// <summary>要发送的内容（<c>hex:</c> 或文本语法）。</summary>
    [ObservableProperty]
    private string _payloadText = "hex:";

    [ObservableProperty]
    private EncodingChoice _payloadEncoding = EncodingChoice.Utf8;

    [ObservableProperty]
    private ChecksumKind _checksum = ChecksumKind.None;

    /// <summary>定时发送的间隔（毫秒），10 到 3600000。</summary>
    [ObservableProperty]
    private int _periodicIntervalMs = 1000;

    /// <summary>定时发送的次数，0 表示一直发送直到停止（0 到 1000000）。</summary>
    [ObservableProperty]
    private int _periodicCount;

    [ObservableProperty]
    private bool _isPeriodicRunning;

    /// <summary>统计的一行文字（每秒刷新）。</summary>
    [ObservableProperty]
    private string _statisticsText = string.Empty;

    public IAsyncRelayCommand SendCommand { get; }

    public IAsyncRelayCommand RequestCommand { get; }

    public IAsyncRelayCommand ReceiveCommand { get; }

    public IRelayCommand StartPeriodicCommand { get; }

    public IRelayCommand StopPeriodicCommand { get; }

    /// <summary>操作记录里显示的目标（端点）。</summary>
    public abstract string Target { get; }

    /// <summary>当前连接的库通道；未连接时为 null。子类需要按类型调用库的特有方法时使用（例如 UDP 的 SendToAsync）。</summary>
    protected IClientChannel? CurrentChannel => _channel;

    /// <summary>创建库的通道；每次连接都创建新的实例（DA-D9）。参数不合法时抛出 <see cref="ArgumentException"/>。</summary>
    protected abstract IClientChannel CreateChannel();

    /// <summary>发送一帧。UDP 在非定向模式下覆盖它，发送到指定的地址。</summary>
    protected virtual Task<CommResult> SendCoreAsync(byte[] payload, CancellationToken cancellationToken) =>
        _channel is { } channel ? channel.SendAsync(payload, cancellationToken) : Task.FromResult(NotConnected<CommResult>());

    /// <summary>发送并等待应答。</summary>
    protected virtual Task<CommResult<byte[]>> RequestCoreAsync(byte[] payload, RequestOptions options, CancellationToken cancellationToken) =>
        _channel is { } channel ? channel.RequestAsync(payload, options, cancellationToken) : Task.FromResult(NotConnected<CommResult<byte[]>>());

    /// <summary>等待下一帧。</summary>
    protected virtual Task<CommResult<byte[]>> ReceiveCoreAsync(RequestOptions options, CancellationToken cancellationToken) =>
        _channel is { } channel ? channel.ReceiveAsync(options, cancellationToken) : Task.FromResult(NotConnected<CommResult<byte[]>>());

    protected override async Task ConnectCoreAsync(CancellationToken cancellationToken)
    {
        if (!Settings.IsValid)
        {
            throw new CommFailureException(CommResult.Fail("参数有误，请先修正标红的字段。", CommErrorKind.InvalidRequest));
        }

        await CloseChannelAsync(CancellationToken.None);
        IClientChannel channel = CreateChannelOrFail();
        _channel = channel;
        // 事件在线程池上触发，可能在同步设置的状态之后才被处理：这里读取通道的当前状态，而不是事件里的快照，避免旧的状态覆盖新的状态。
        channel.StateChanged += (_, _) => PostToUi(() =>
        {
            if (ReferenceEquals(_channel, channel))
            {
                OnChannelStateChanged(channel.State);
            }
        });
        channel.FrameReceived += (_, e) => PostToUi(() => AddReceived(e));

        State = SessionState.Connecting;
        CommResult result = await channel.ConnectAsync(cancellationToken);
        if (!result.IsSuccess)
        {
            await CloseChannelAsync(CancellationToken.None);
            State = SessionState.NotConnected;
            throw new CommFailureException(result);
        }

        State = SessionState.Connected;
    }

    protected override async Task DisconnectCoreAsync(CancellationToken cancellationToken)
    {
        StopPeriodic();
        await CloseChannelAsync(cancellationToken);
        State = SessionState.NotConnected;
    }

    public override async ValueTask DisposeAsync()
    {
        StopPeriodic();
        try
        {
            await base.DisposeAsync();
        }
        finally
        {
            await _statistics.DisposeAsync();
        }
    }

    /// <summary>把库的通道状态映射为页签状态（计划第 5.2 节）。</summary>
    protected void OnChannelStateChanged(ConnectionState state) => State = ConnectionStates.ToSessionState(state);

    /// <summary>请求与接收使用的选项：超时来自超时编辑器；匹配器模式下按规则匹配应答。</summary>
    protected RequestOptions ToRequestOptions() => new()
    {
        Timeout = Settings.Timeouts.RequestTimeout,
        Matcher = Settings.Correlation.Mode == CorrelationMode.Matcher
            ? new ConfigurableResponseMatcher(Settings.Correlation.ToMatchRule())
            : null,
    };

    private IClientChannel CreateChannelOrFail()
    {
        try
        {
            return CreateChannel();
        }
        catch (ArgumentException ex)
        {
            throw new CommFailureException(CommResult.Fail(ex.Message, CommErrorKind.InvalidRequest));
        }
    }

    private async Task CloseChannelAsync(CancellationToken cancellationToken)
    {
        if (_channel is not { } channel)
        {
            return;
        }

        _channel = null;
        try
        {
            await channel.DisconnectAsync(cancellationToken);
        }
        finally
        {
            await channel.DisposeAsync();
        }
    }

    private static T NotConnected<T>()
        where T : class => typeof(T) == typeof(CommResult)
            ? (T)(object)CommResult.Fail("The channel is not connected.", CommErrorKind.NotConnected)
            : (T)(object)CommResult<byte[]>.Fail("The channel is not connected.", CommErrorKind.NotConnected);

    /// <summary>把输入转换为要发送的字节：先按编码解析，再追加校验（计划 DA-D12）。</summary>
    private byte[] PreparePayload() => Checksums.Append(Checksum, ByteInput.Parse(PayloadText, PayloadEncoding));

    private async Task SendOnceAsync()
    {
        if (!TryPreparePayload(out byte[] payload))
        {
            return;
        }

        DateTimeOffset started = Dependencies.Clock.GetLocalNow();
        CommResult result = await SendCoreAsync(payload, CancellationToken.None);
        RecordOperation(OperationRecords.From(started, "发送", Target, TimeSpan.Zero, result));
        if (result.IsSuccess)
        {
            AddSendHistory(PayloadText);
        }
    }

    private async Task RequestOnceAsync()
    {
        if (!TryPreparePayload(out byte[] payload))
        {
            return;
        }

        DateTimeOffset started = Dependencies.Clock.GetLocalNow();
        CommResult<byte[]> result = await RequestCoreAsync(payload, ToRequestOptions(), CancellationToken.None);
        RecordOperation(OperationRecords.From(started, "请求", Target, TimeSpan.Zero, result));
        if (result.IsSuccess)
        {
            ShowReceived(started, result.Data!, Target);
            AddSendHistory(PayloadText);
        }
    }

    private async Task ReceiveOnceAsync()
    {
        DateTimeOffset started = Dependencies.Clock.GetLocalNow();
        CommResult<byte[]> result = await ReceiveCoreAsync(ToRequestOptions(), CancellationToken.None);
        RecordOperation(OperationRecords.From(started, "接收", Target, TimeSpan.Zero, result));
        if (result.IsSuccess)
        {
            ShowReceived(started, result.Data!, Target);
        }
    }

    /// <summary>运行状态变化时刷新开始与停止命令的可用状态（按钮随之切换）。</summary>
    partial void OnIsPeriodicRunningChanged(bool value)
    {
        StartPeriodicCommand.NotifyCanExecuteChanged();
        StopPeriodicCommand.NotifyCanExecuteChanged();
    }

    private void StartPeriodic()
    {
        // 命令的可用状态由 IsPeriodicRunning 控制；这里再守一次，防止第二个循环无法被停止。
        if (IsPeriodicRunning)
        {
            return;
        }

        if (PeriodicIntervalMs is < 10 or > 3600000 || PeriodicCount is < 0 or > 1000000)
        {
            RecordOperation(new OperationRecord(Dependencies.Clock.GetLocalNow(), "定时发送", Target, TimeSpan.Zero, false,
                CommErrorKind.InvalidRequest, null, "发送间隔必须在 10 到 3600000 毫秒之间，次数必须在 0 到 1000000 之间。"));
            return;
        }

        if (!TryPreparePayload(out byte[] payload))
        {
            return;
        }

        var stop = new CancellationTokenSource();
        _periodicStop = stop;
        IsPeriodicRunning = true;
        _ = RunPeriodicAsync(payload, PeriodicCount, PeriodicIntervalMs, stop);
    }

    /// <summary>
    /// 定时发送：每次发送之间等待间隔；停止、断开或释放后不再发送（停止时已经开始的一次发送可能仍会完成）。
    /// 失败记入操作记录；其他异常同样记入，不逃出后台任务（U7）。
    /// </summary>
    private async Task RunPeriodicAsync(byte[] payload, int count, int intervalMs, CancellationTokenSource stop)
    {
        try
        {
            int sent = 0;
            while (!stop.IsCancellationRequested && (count == 0 || sent < count))
            {
                CommResult result = await SendCoreAsync(payload, stop.Token).ConfigureAwait(false);
                sent++;
                if (!result.IsSuccess)
                {
                    DateTimeOffset failedAt = Dependencies.Clock.GetLocalNow();
                    PostToUi(() => RecordOperation(OperationRecords.From(failedAt, "定时发送", Target, TimeSpan.Zero, result)));
                }

                await Task.Delay(intervalMs, stop.Token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            DateTimeOffset failedAt = Dependencies.Clock.GetLocalNow();
            PostToUi(() => RecordOperation(new OperationRecord(failedAt, "定时发送", Target, TimeSpan.Zero, false, CommErrorKind.Unspecified, null, ex.Message)));
        }
        finally
        {
            // 只有仍然是当前一次运行时才复位：已被停止并重新开始的旧循环不能把新循环的状态清掉。
            PostToUi(() =>
            {
                if (ReferenceEquals(_periodicStop, stop))
                {
                    _periodicStop = null;
                    IsPeriodicRunning = false;
                }
            });
        }
    }

    private void StopPeriodic()
    {
        if (_periodicStop is { } stop)
        {
            _periodicStop = null;
            stop.Cancel();
        }

        IsPeriodicRunning = false;
    }

    private bool TryPreparePayload(out byte[] payload)
    {
        try
        {
            payload = PreparePayload();
            return true;
        }
        catch (FormatException ex)
        {
            RecordOperation(new OperationRecord(Dependencies.Clock.GetLocalNow(), "发送", Target, TimeSpan.Zero, false, CommErrorKind.InvalidRequest, null, ex.Message));
            payload = [];
            return false;
        }
    }

    private void AddReceived(FrameReceivedEventArgs e) => ShowReceived(e.ReceivedAt, e.Data, e.RemoteEndPoint?.ToString());

    private void ShowReceived(DateTimeOffset time, byte[] data, string? source) =>
        ReceivedFrames.InsertNewest(new ReceivedFrame(time, data, source), MaxReceivedFrames);

    private void AddSendHistory(string text) => SendHistory.InsertNewest(text, MaxSendHistory);

    private void RefreshStatistics()
    {
        ConnectionStatistics? stats = _channel?.Statistics;
        StatisticsText = stats is null
            ? string.Empty
            : $"发送 {stats.FramesSent} 帧 / {stats.BytesSent} 字节（最后 {FormatTime(stats.LastSentAt)}），" +
              $"接收 {stats.FramesReceived} 帧 / {stats.BytesReceived} 字节（最后 {FormatTime(stats.LastReceivedAt)}），" +
              $"丢弃 {stats.FramesDropped}，协议错误 {stats.ProtocolErrors}，重连 {stats.ReconnectCount}，" +
              $"连续心跳失败 {stats.ConsecutiveHeartbeatFailures}，连接于 {FormatTime(stats.ConnectedSince)}";
    }

    private static string FormatTime(DateTimeOffset? time) => time is { } value ? value.ToLocalTime().ToString("HH:mm:ss") : "无";
}

/// <summary>收到的一帧：时间、字节与来源端点（UDP 为发送方，TCP 为对端）。</summary>
public sealed record ReceivedFrame(DateTimeOffset Time, byte[] Data, string? Source)
{
    /// <summary>十六进制显示（库的格式）。</summary>
    public string Hex => HexFormatter.ToHex(Data);
}
