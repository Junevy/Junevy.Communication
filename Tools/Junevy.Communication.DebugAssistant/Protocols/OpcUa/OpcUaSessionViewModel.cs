using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Junevy.Communication.Core.Results;
using Junevy.Communication.DebugAssistant.Common;
using Junevy.Communication.DebugAssistant.Sessions;
using Junevy.Communication.DebugAssistant.Shell;
using Junevy.Communication.DebugAssistant.Workspace;
using Junevy.Communication.OpcUa;

namespace Junevy.Communication.DebugAssistant.Protocols.OpcUa;

/// <summary>
/// OPC UA 客户端页（计划第 7.8 节）：连接选项（只支持 None 与匿名，界面醒目提示）；从 <c>i=85</c> 懒加载浏览；读写列表
/// （批量读的逐项结果、按所选 .NET 类型写入且不做转换、BadTypeMismatch 等逐项状态）；订阅（每项的创建状态、实时通知、释放）。
/// 库的状态与通知先经 <see cref="SessionViewModel.PostToUi"/> 回到界面线程；已断开、已替换或已释放的订阅的通知被忽略。
/// </summary>
public sealed partial class OpcUaSessionViewModel : SessionViewModel
{
    private const string ObjectsFolderNodeId = "i=85";
    private const int MaxNotifications = 200;
    private const string NotConnectedMessage = "页签没有连接，请先连接。";

    private OpcUaClient? _client;
    private OpcUaSubscription? _subscription;
    private object? _subscriptionToken;

    public OpcUaSessionViewModel(ProtocolDescriptor descriptor, SessionServices dependencies)
        : base(descriptor, dependencies)
    {
        BrowseRootCommand = new AsyncRelayCommand(BrowseRootAsync, () => IsLinked);
        AddNodeCommand = new RelayCommand(AddNode);
        RemoveNodeCommand = new RelayCommand<OpcUaReadRow?>(RemoveNode);
        AddBrowsedNodeCommand = new RelayCommand<OpcUaNodeItem?>(AddBrowsedNode);
        ReadCommand = new AsyncRelayCommand(ReadAsync, () => IsLinked);
        WriteCommand = new AsyncRelayCommand(WriteAsync, () => IsLinked);
        SubscribeCommand = new AsyncRelayCommand(SubscribeAsync, () => IsLinked);
        ReleaseSubscriptionCommand = new AsyncRelayCommand(ReleaseSubscriptionAsync);
        PropertyChanged += OnOwnPropertyChanged;
    }

    /// <summary>浏览树的根（i=85 的子节点）。</summary>
    public ObservableCollection<OpcUaNodeItem> BrowseRoots { get; } = [];

    /// <summary>读写列表：批量读与写入都针对这里的行。</summary>
    public ObservableCollection<OpcUaReadRow> Items { get; } = [];

    /// <summary>当前订阅的各项与服务器给出的创建状态。</summary>
    public ObservableCollection<OpcUaSubscriptionRow> SubscribedItems { get; } = [];

    /// <summary>实时通知，最新的在最前。</summary>
    public ObservableCollection<OpcUaNotificationRow> Notifications { get; } = [];

    public IReadOnlyList<OpcUaWriteType> WriteTypeChoices { get; } = Enum.GetValues<OpcUaWriteType>();

    /// <summary>已连接；浏览、读写与订阅只在这时可用。</summary>
    public bool IsLinked => State == SessionState.Connected;

    [ObservableProperty]
    private string _endpointUrl = "opc.tcp://127.0.0.1:4840";

    [ObservableProperty]
    private string _applicationName = "Junevy.DebugAssistant";

    [ObservableProperty]
    private string _applicationUri = string.Empty;

    [ObservableProperty]
    private string _pkiRoot = OpcUaClientOptions.DefaultPkiRoot;

    [ObservableProperty]
    private int _connectTimeout = 10000;

    [ObservableProperty]
    private int _operationTimeout = 10000;

    [ObservableProperty]
    private int _sessionTimeout = 60000;

    [ObservableProperty]
    private int _keepAliveInterval = 5000;

    [ObservableProperty]
    private int _reconnectInterval = 1000;

    [ObservableProperty]
    private int _maxReconnectInterval = 30000;

    /// <summary>订阅的发布间隔，毫秒。</summary>
    [ObservableProperty]
    private int _publishingInterval = 1000;

    /// <summary>采样间隔，毫秒；空表示跟随发布间隔。</summary>
    [ObservableProperty]
    private int? _samplingInterval;

    [ObservableProperty]
    private uint _queueSize = 1;

    [ObservableProperty]
    private bool _discardOldest = true;

    /// <summary>库的会话状态的中文名称。</summary>
    [ObservableProperty]
    private string _opcUaStateText = OpcUaSupport.StateName(OpcUaSessionState.Disconnected);

    /// <summary>最近一次状态变化的原因（库给出，没有时为空）。</summary>
    [ObservableProperty]
    private string _stateReason = string.Empty;

    [ObservableProperty]
    private string _browseMessage = string.Empty;

    [ObservableProperty]
    private string _newNodeId = string.Empty;

    [ObservableProperty]
    private OpcUaReadRow? _selectedItem;

    /// <summary>读写列表的说明：读取完成、写入结果，或失败的原因。</summary>
    [ObservableProperty]
    private string _listMessage = string.Empty;

    [ObservableProperty]
    private string _subscriptionMessage = string.Empty;

    public IAsyncRelayCommand BrowseRootCommand { get; }

    public IRelayCommand AddNodeCommand { get; }

    public IRelayCommand<OpcUaReadRow?> RemoveNodeCommand { get; }

    public IRelayCommand<OpcUaNodeItem?> AddBrowsedNodeCommand { get; }

    public IAsyncRelayCommand ReadCommand { get; }

    public IAsyncRelayCommand WriteCommand { get; }

    public IAsyncRelayCommand SubscribeCommand { get; }

    public IAsyncRelayCommand ReleaseSubscriptionCommand { get; }

    public override SessionSettings CaptureSettings() => Capture();

    public override void ApplySettings(SessionSettings settings)
    {
        if (settings is not OpcUaSettings saved)
        {
            throw new ArgumentException("The settings do not belong to an OPC UA page.", nameof(settings));
        }

        EndpointUrl = saved.EndpointUrl ?? string.Empty;
        ApplicationName = saved.ApplicationName;
        ApplicationUri = saved.ApplicationUri ?? string.Empty;
        PkiRoot = saved.PkiRoot;
        ConnectTimeout = saved.ConnectTimeout;
        OperationTimeout = saved.OperationTimeout;
        SessionTimeout = saved.SessionTimeout;
        KeepAliveInterval = saved.KeepAliveInterval;
        ReconnectInterval = saved.ReconnectInterval;
        MaxReconnectInterval = saved.MaxReconnectInterval;
        PublishingInterval = saved.PublishingInterval;
        SamplingInterval = saved.SamplingInterval;
        QueueSize = saved.QueueSize;
        DiscardOldest = saved.DiscardOldest;
    }

    protected override async Task ConnectCoreAsync(CancellationToken cancellationToken)
    {
        // 上一个客户端可能是故障后留下的（重连关闭）：连接前先释放它。
        await ReleaseClientAsync().ConfigureAwait(true);
        OpcUaClient client = CreateClient(Capture());
        _client = client;
        client.StateChanged += (_, e) => PostToUi(() =>
        {
            if (ReferenceEquals(_client, client))
            {
                ApplyState(e);
            }
        });

        State = SessionState.Connecting;
        CommResult result;
        try
        {
            result = await client.ConnectAsync(cancellationToken).ConfigureAwait(true);
        }
        catch (Exception)
        {
            await ReleaseClientAsync(client).ConfigureAwait(true);
            ResetState();
            throw;
        }

        if (!result.IsSuccess)
        {
            await ReleaseClientAsync(client).ConfigureAwait(true);
            ResetState();
            throw new CommFailureException(result);
        }

        State = SessionState.Connected;
        OpcUaStateText = OpcUaSupport.StateName(OpcUaSessionState.Connected);
    }

    protected override async Task DisconnectCoreAsync(CancellationToken cancellationToken)
    {
        if (_client is not { } client)
        {
            ResetState();
            return;
        }

        _client = null;
        BrowseRoots.Clear();
        await ReleaseSubscriptionCoreAsync().ConfigureAwait(true);
        try
        {
            await client.DisconnectAsync(cancellationToken).ConfigureAwait(true);
        }
        finally
        {
            await client.DisposeAsync().ConfigureAwait(true);
            ResetState();
        }
    }

    private OpcUaSettings Capture() => new(
        Optional(EndpointUrl),
        ApplicationName.Trim(),
        Optional(ApplicationUri),
        PkiRoot.Trim(),
        ConnectTimeout,
        OperationTimeout,
        SessionTimeout,
        KeepAliveInterval,
        ReconnectInterval,
        MaxReconnectInterval,
        PublishingInterval,
        SamplingInterval,
        QueueSize,
        DiscardOldest);

    private static string? Optional(string text) => string.IsNullOrWhiteSpace(text) ? null : text.Trim();

    // 端点为空先在页面上拒绝；库的其它选项校验失败时抛出 ArgumentException，这里转成请求无效，与其它页签一致。
    private OpcUaClient CreateClient(OpcUaSettings settings)
    {
        if (string.IsNullOrEmpty(settings.EndpointUrl))
        {
            throw new CommFailureException(CommResult.Fail("端点地址不能为空。", CommErrorKind.InvalidRequest));
        }

        var options = new OpcUaClientOptions
        {
            EndpointUrl = settings.EndpointUrl,
            ApplicationName = settings.ApplicationName,
            ApplicationUri = settings.ApplicationUri,
            PkiRoot = settings.PkiRoot,
            ConnectTimeout = settings.ConnectTimeout,
            OperationTimeout = settings.OperationTimeout,
            SessionTimeout = settings.SessionTimeout,
            KeepAliveInterval = settings.KeepAliveInterval,
            ReconnectInterval = settings.ReconnectInterval,
            MaxReconnectInterval = settings.MaxReconnectInterval,
        };

        try
        {
            return OpcUaClient.Create(options, SessionLoggerFactory);
        }
        catch (ArgumentException ex)
        {
            throw new CommFailureException(CommResult.Fail(ex.Message, CommErrorKind.InvalidRequest));
        }
    }

    // 释放当前客户端（或指定的那一个）。订阅在服务器上随会话结束，这里只丢弃本地引用，之后它的通知被忽略。
    private async Task ReleaseClientAsync(OpcUaClient? expected = null)
    {
        if (_client is not { } client || (expected is not null && !ReferenceEquals(client, expected)))
        {
            return;
        }

        _client = null;
        BrowseRoots.Clear();
        ClearSubscriptionState();
        await client.DisposeAsync().ConfigureAwait(true);
    }

    private void ApplyState(OpcUaSessionStateChangedEventArgs e)
    {
        OpcUaStateText = OpcUaSupport.StateName(e.Current);
        StateReason = e.Reason ?? string.Empty;
        State = OpcUaSupport.ToSessionState(e.Current, e.Reason);
        if (e.Current == OpcUaSessionState.Disconnected)
        {
            ClearSubscriptionState();
        }
    }

    private void ResetState()
    {
        State = SessionState.NotConnected;
        OpcUaStateText = OpcUaSupport.StateName(OpcUaSessionState.Disconnected);
        StateReason = string.Empty;
    }

    private async Task BrowseRootAsync()
    {
        BrowseRoots.Clear();
        OpcUaNodeItem root = CreateNode(ObjectsFolderNodeId, "Objects", "Objects", OpcUaNodeClass.Object);
        BrowseRoots.Add(root);
        await LoadChildrenAsync(root).ConfigureAwait(true);
        if (root.IsLoaded)
        {
            root.IsExpanded = true;
        }
    }

    private OpcUaNodeItem CreateNode(string nodeId, string browseName, string displayName, OpcUaNodeClass nodeClass) =>
        new(nodeId, browseName, displayName, nodeClass, LoadChildrenAsync);

    // 浏览一个节点的正向层级引用，作为它的子节点。失败记录在消息区与操作记录里，节点保留占位以便重试。
    private async Task LoadChildrenAsync(OpcUaNodeItem node)
    {
        if (_client is not { } client)
        {
            BrowseMessage = NotConnectedMessage;
            return;
        }

        node.IsLoading = true;
        DateTimeOffset started = Dependencies.Clock.GetLocalNow();
        Stopwatch clock = Stopwatch.StartNew();
        CommResult<IReadOnlyList<OpcUaReference>> result = await client.BrowseAsync(node.NodeId).ConfigureAwait(true);
        RecordOperation(OperationRecords.From(started, "浏览", node.NodeId, clock.Elapsed, result));
        if (!ReferenceEquals(_client, client))
        {
            node.FailLoad();
            return;
        }

        if (!result.IsSuccess || result.Data is null)
        {
            node.FailLoad();
            BrowseMessage = ResultText.Failure(result.ErrorKind, result.ErrorMessage);
            return;
        }

        node.ApplyChildren(result.Data
            .Select(reference => CreateNode(reference.NodeId, reference.BrowseName, reference.DisplayName, reference.NodeClass))
            .ToArray());
        BrowseMessage = $"{node.DisplayName}：{result.Data.Count} 个子节点。";
    }

    private void AddBrowsedNode(OpcUaNodeItem? node)
    {
        if (node is null || node.IsPlaceholder)
        {
            return;
        }

        if (!node.IsVariable)
        {
            BrowseMessage = $"{node.DisplayName} 不是变量（{node.NodeClass}），不能加入读写列表。";
            return;
        }

        Items.Add(new OpcUaReadRow(node.NodeId));
        ListMessage = $"已加入 {node.NodeId}。";
    }

    private void AddNode()
    {
        string nodeId = NewNodeId.Trim();
        if (nodeId.Length == 0)
        {
            ListMessage = "NodeId 不能为空。";
            return;
        }

        Items.Add(new OpcUaReadRow(nodeId));
        NewNodeId = string.Empty;
    }

    private void RemoveNode(OpcUaReadRow? row)
    {
        if (row is not null)
        {
            Items.Remove(row);
        }
    }

    // 批量读：每一项有自己的值、类型、状态与时间戳；未知节点只是那一项的状态，不让整个调用失败。
    private async Task ReadAsync()
    {
        if (_client is not { } client)
        {
            ListMessage = NotConnectedMessage;
            return;
        }

        if (Items.Count == 0)
        {
            ListMessage = "读写列表为空，请先添加节点或从浏览树双击变量。";
            return;
        }

        OpcUaReadRow[] rows = Items.ToArray();
        string[] nodeIds = rows.Select(row => row.NodeId).ToArray();
        DateTimeOffset started = Dependencies.Clock.GetLocalNow();
        Stopwatch clock = Stopwatch.StartNew();
        CommResult<IReadOnlyList<OpcUaValue>> result = await client.ReadAsync(nodeIds).ConfigureAwait(true);
        RecordOperation(OperationRecords.From(started, "读取", $"{nodeIds.Length} 项", clock.Elapsed, result));
        if (!ReferenceEquals(_client, client))
        {
            return;
        }

        if (!result.IsSuccess || result.Data is null || result.Data.Count != rows.Length)
        {
            ListMessage = result.IsSuccess
                ? "读取结果的项数与请求不一致。"
                : ResultText.Failure(result.ErrorKind, result.ErrorMessage);
            return;
        }

        for (int i = 0; i < rows.Length; i++)
        {
            rows[i].ApplyRead(result.Data[i]);
        }

        ListMessage = $"已读取 {rows.Length} 项，逐项结果见读写列表。";
    }

    // 写入：输入在发送前全部解析；任何一项无法解析都不发送，避免批量只写了一部分。
    private async Task WriteAsync()
    {
        if (_client is not { } client)
        {
            ListMessage = NotConnectedMessage;
            return;
        }

        OpcUaReadRow[] pending = Items.Where(row => row.WriteText.Length > 0).ToArray();
        if (pending.Length == 0)
        {
            ListMessage = "没有填写要写入的值（在选中行的写入区填写）。";
            return;
        }

        var values = new OpcUaWriteValue[pending.Length];
        bool valid = true;
        for (int i = 0; i < pending.Length; i++)
        {
            try
            {
                values[i] = new OpcUaWriteValue(
                    pending[i].NodeId,
                    OpcUaSupport.ParseValue(pending[i].WriteType, pending[i].IsArray, pending[i].WriteText));
                pending[i].WriteStatus = string.Empty;
            }
            catch (FormatException ex)
            {
                pending[i].WriteStatus = $"输入无效：{ex.Message}";
                valid = false;
            }
        }

        if (!valid)
        {
            ListMessage = "有写入值无法按所选类型解析，未发送任何写入。";
            return;
        }

        DateTimeOffset started = Dependencies.Clock.GetLocalNow();
        Stopwatch clock = Stopwatch.StartNew();
        CommResult<IReadOnlyList<OpcUaStatusCode>> result = await client.WriteAsync(values).ConfigureAwait(true);
        RecordOperation(OperationRecords.From(started, "写入", $"{values.Length} 项", clock.Elapsed, result));
        if (!ReferenceEquals(_client, client))
        {
            return;
        }

        if (!result.IsSuccess || result.Data is null || result.Data.Count != pending.Length)
        {
            ListMessage = result.IsSuccess
                ? "写入结果的项数与请求不一致。"
                : ResultText.Failure(result.ErrorKind, result.ErrorMessage);
            return;
        }

        for (int i = 0; i < pending.Length; i++)
        {
            pending[i].WriteStatus = OpcUaSupport.DescribeStatus(result.Data[i]);
        }

        ListMessage = $"已写入 {pending.Length} 项，逐项结果见写入结果。";
    }

    // 订阅读写列表中的全部节点。同一时间只保留一个订阅；通知通过一个令牌过滤，释放之后不会再交付。
    private async Task SubscribeAsync()
    {
        if (_client is not { } client)
        {
            SubscriptionMessage = NotConnectedMessage;
            return;
        }

        if (_subscription is not null)
        {
            SubscriptionMessage = "已有订阅，请先释放再订阅。";
            return;
        }

        if (Items.Count == 0)
        {
            SubscriptionMessage = "读写列表为空，没有可订阅的节点。";
            return;
        }

        string[] nodeIds = Items.Select(row => row.NodeId).ToArray();
        var options = new OpcUaSubscriptionOptions
        {
            PublishingInterval = PublishingInterval,
            SamplingInterval = SamplingInterval,
            QueueSize = QueueSize,
            DiscardOldest = DiscardOldest,
        };
        var token = new object();
        _subscriptionToken = token;
        DateTimeOffset started = Dependencies.Clock.GetLocalNow();
        Stopwatch clock = Stopwatch.StartNew();
        CommResult<OpcUaSubscription> result;
        try
        {
            result = await client.SubscribeAsync(
                nodeIds,
                (_, e) => PostToUi(() =>
                {
                    if (ReferenceEquals(_subscriptionToken, token))
                    {
                        RecordNotification(e);
                    }
                }),
                options).ConfigureAwait(true);
        }
        catch (ArgumentException ex)
        {
            _subscriptionToken = null;
            SubscriptionMessage = ResultText.Failure(CommErrorKind.InvalidRequest, ex.Message);
            return;
        }

        RecordOperation(OperationRecords.From(started, "订阅", $"{nodeIds.Length} 项", clock.Elapsed, result));
        if (!result.IsSuccess || result.Data is null)
        {
            if (ReferenceEquals(_subscriptionToken, token))
            {
                _subscriptionToken = null;
            }

            SubscriptionMessage = ResultText.Failure(result.ErrorKind, result.ErrorMessage);
            return;
        }

        OpcUaSubscription subscription = result.Data;
        if (!ReferenceEquals(_subscriptionToken, token) || !ReferenceEquals(_client, client))
        {
            // 等待期间释放了订阅或断开了连接：刚建立的订阅不再保留。
            await subscription.DisposeAsync().ConfigureAwait(true);
            return;
        }

        _subscription = subscription;
        for (int i = 0; i < nodeIds.Length; i++)
        {
            SubscribedItems.Add(new OpcUaSubscriptionRow(nodeIds[i], OpcUaSupport.DescribeStatus(subscription.ItemStatuses[i])));
        }

        SubscriptionMessage = $"已订阅 {nodeIds.Length} 项，服务器的初始值会随后到达。";
    }

    private async Task ReleaseSubscriptionAsync()
    {
        if (_subscription is null)
        {
            SubscriptionMessage = "当前没有订阅。";
            return;
        }

        await ReleaseSubscriptionCoreAsync().ConfigureAwait(true);
        SubscriptionMessage = "已释放订阅，之后不再收到通知。";
    }

    private async Task ReleaseSubscriptionCoreAsync()
    {
        OpcUaSubscription? subscription = _subscription;
        ClearSubscriptionState();
        if (subscription is not null)
        {
            await subscription.DisposeAsync().ConfigureAwait(true);
        }
    }

    private void ClearSubscriptionState()
    {
        _subscription = null;
        _subscriptionToken = null;
        SubscribedItems.Clear();
    }

    private void RecordNotification(OpcUaDataChangedEventArgs e)
    {
        OpcUaValue value = e.Value;
        var row = new OpcUaNotificationRow(
            Dependencies.Clock.GetLocalNow().ToString("HH:mm:ss.fff", CultureInfo.InvariantCulture),
            e.NodeId,
            OpcUaSupport.FormatValue(value),
            OpcUaSupport.DescribeStatus(value.Status),
            OpcUaSupport.FormatTimestamp(value.SourceTimestamp),
            OpcUaSupport.FormatTimestamp(value.ServerTimestamp));
        Notifications.InsertNewest(row, MaxNotifications);
    }

    private void OnOwnPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(State))
        {
            return;
        }

        OnPropertyChanged(nameof(IsLinked));
        BrowseRootCommand.NotifyCanExecuteChanged();
        ReadCommand.NotifyCanExecuteChanged();
        WriteCommand.NotifyCanExecuteChanged();
        SubscribeCommand.NotifyCanExecuteChanged();
    }
}
