using System.Collections.ObjectModel;
using System.Diagnostics;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Junevy.Communication.Core.Results;
using Junevy.Communication.DebugAssistant.Logging;
using Junevy.Communication.DebugAssistant.Shell;
using Microsoft.Extensions.Logging;

namespace Junevy.Communication.DebugAssistant.Sessions;

/// <summary>
/// 调试页签的视图模型基类（计划第 5.2 节）。子类负责参数与库配置的映射、连接与断开（<see cref="ConnectCoreAsync"/>、
/// <see cref="DisconnectCoreAsync"/>），以及协议特有的命令。
/// 所有成员只在界面线程上使用，除非另有说明；库的事件先经 <see cref="PostToUi"/> 回到界面线程。
/// </summary>
public abstract partial class SessionViewModel : ObservableObject, IAsyncDisposable
{
    private const int MaxOperationRecords = 500;

    private readonly ILoggerFactory _loggerFactory;
    private readonly SessionLogFlusher _flusher;
    private readonly ILogger _logger;
    private SessionState _state = SessionState.NotConnected;

    protected SessionViewModel(ProtocolDescriptor descriptor, SessionServices dependencies)
    {
        Descriptor = descriptor;
        Dependencies = dependencies;
        Title = descriptor.ShortName;
        Log = new SessionLog();
        LogPanel = new LogPanelViewModel(Log, dependencies.Files, dependencies.Notifications);
        _loggerFactory = LoggerFactory.Create(builder =>
        {
            builder.SetMinimumLevel(LogLevel.Debug);
            builder.AddProvider(new SessionLogSink(Log, dependencies.Clock));
        });
        _logger = _loggerFactory.CreateLogger(GetType().FullName ?? descriptor.ShortName);
        _flusher = new SessionLogFlusher(Log, dependencies.Dispatcher);

        var connect = new AsyncRelayCommand(ConnectOperationAsync, () => IsEditable);
        var disconnect = new AsyncRelayCommand(DisconnectOperationAsync, () => IsConnectedLike);
        ConnectCommand = connect;
        ConnectCancelCommand = Cancelable(connect);
        DisconnectCommand = disconnect;
        DisconnectCancelCommand = Cancelable(disconnect);
    }

    public ProtocolDescriptor Descriptor { get; }

    public ProtocolKind Kind => Descriptor.Kind;

    /// <summary>页签标题，可以在界面上改名（计划 DA-D8）。</summary>
    [ObservableProperty]
    private string _title = string.Empty;

    /// <summary>所有页签共用的依赖（界面派发与时钟）。</summary>
    protected SessionServices Dependencies { get; }

    /// <summary>页签私有的报文与日志。</summary>
    public SessionLog Log { get; }

    /// <summary>页签底部的报文与日志面板。</summary>
    public LogPanelViewModel LogPanel { get; }

    /// <summary>页签私有的日志工厂（最低级别 Debug，挂 <see cref="SessionLogSink"/>）；协议客户端用它创建。</summary>
    public ILoggerFactory SessionLoggerFactory => _loggerFactory;

    /// <summary>页签的运行状态。只能由子类设置。</summary>
    public SessionState State
    {
        get => _state;
        protected set
        {
            if (SetProperty(ref _state, value))
            {
                OnPropertyChanged(nameof(StateText));
                OnPropertyChanged(nameof(IsEditable));
                ConnectCommand.NotifyCanExecuteChanged();
                DisconnectCommand.NotifyCanExecuteChanged();
            }
        }
    }

    public string StateText => SessionStateNames.Of(State);

    /// <summary>参数区是否可编辑：未连接、已停止或故障时为 true（计划 DA-D9）。</summary>
    public bool IsEditable => State is SessionState.NotConnected or SessionState.Stopped or SessionState.Faulted;

    /// <summary>操作记录，最新的在最前（计划 U6），超过上限时丢弃最旧的。</summary>
    public ObservableCollection<OperationRecord> Operations { get; } = [];

    public IAsyncRelayCommand ConnectCommand { get; }

    public IRelayCommand ConnectCancelCommand { get; }

    public IAsyncRelayCommand DisconnectCommand { get; }

    public IRelayCommand DisconnectCancelCommand { get; }

    private bool IsConnectedLike => State is SessionState.Connected or SessionState.Reconnecting or SessionState.Running;

    /// <summary>连接：创建库的客户端（或启动服务端），并把库的状态映射到 <see cref="State"/>。</summary>
    protected virtual Task ConnectCoreAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    /// <summary>断开：断开客户端、停止服务端或模拟器。关闭页签时也会调用。</summary>
    protected virtual Task DisconnectCoreAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    /// <summary>释放会话：先断开，再停止日志刷新并释放日志工厂。</summary>
    public virtual async ValueTask DisposeAsync()
    {
        try
        {
            await DisconnectCoreAsync(CancellationToken.None);
        }
        finally
        {
            await _flusher.DisposeAsync();
            _loggerFactory.Dispose();
        }
    }

    /// <summary>记录一次操作（界面线程）。</summary>
    protected void RecordOperation(OperationRecord record)
    {
        Operations.Insert(0, record);
        while (Operations.Count > MaxOperationRecords)
        {
            Operations.RemoveAt(Operations.Count - 1);
        }
    }

    /// <summary>把工作派发回界面线程（库的事件在线程池上触发时使用）。</summary>
    protected void PostToUi(Action action) => Dependencies.Dispatcher.Post(action);

    /// <summary>取消按钮：只在操作进行中可用。</summary>
    private static RelayCommand Cancelable(AsyncRelayCommand command)
    {
        var cancel = new RelayCommand(command.Cancel, () => command.IsRunning);
        command.CanExecuteChanged += (_, _) => cancel.NotifyCanExecuteChanged();
        return cancel;
    }

    private Task ConnectOperationAsync(CancellationToken cancellationToken) =>
        RunOperationAsync("连接", ConnectCoreAsync, cancellationToken);

    private Task DisconnectOperationAsync(CancellationToken cancellationToken) =>
        RunOperationAsync("断开", DisconnectCoreAsync, cancellationToken);

    /// <summary>
    /// 执行连接或断开。异常不会逃出界面（计划 U7）：取消记为 <see cref="CommErrorKind.Cancelled"/>，
    /// 其他异常记为未分类失败并写入页签日志。
    /// </summary>
    private async Task RunOperationAsync(string operation, Func<CancellationToken, Task> action, CancellationToken cancellationToken)
    {
        DateTimeOffset started = Dependencies.Clock.GetLocalNow();
        Stopwatch clock = Stopwatch.StartNew();
        try
        {
            await action(cancellationToken);
            RecordOperation(new OperationRecord(started, operation, string.Empty, clock.Elapsed, true, CommErrorKind.None, null, string.Empty));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            RecordOperation(new OperationRecord(started, operation, string.Empty, clock.Elapsed, false, CommErrorKind.Cancelled, null, "操作已取消。"));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "{Operation} failed.", operation);
            RecordOperation(new OperationRecord(started, operation, string.Empty, clock.Elapsed, false, CommErrorKind.Unspecified, null, ex.Message));
        }
    }
}
