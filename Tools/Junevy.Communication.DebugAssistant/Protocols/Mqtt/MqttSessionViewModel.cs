using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Junevy.Communication.Core.Results;
using Junevy.Communication.DebugAssistant.Common;
using Junevy.Communication.DebugAssistant.Sessions;
using Junevy.Communication.DebugAssistant.Shell;
using Junevy.Communication.DebugAssistant.Workspace;
using Junevy.Communication.Mqtt;

namespace Junevy.Communication.DebugAssistant.Protocols.Mqtt;

/// <summary>
/// MQTT 客户端页（计划第 7.7 节）：连接选项、TLS、重连与遗嘱；订阅与退订（显示 Broker 授予的 QoS）；发布（文本或十六进制负载、
/// QoS、Retain 与 MQTT 5 属性）；接收列表。库的事件先经 <see cref="SessionViewModel.PostToUi"/> 回到界面线程，
/// 已断开或已被替换的客户端的事件被忽略，因此断开之后不会再出现旧连接的消息或状态。
/// </summary>
public sealed partial class MqttSessionViewModel : SessionViewModel
{
    private const int MaxReceivedMessages = 200;
    private const string NotConnectedMessage = "页签没有连接，请先连接。";

    private MqttClient? _client;

    public MqttSessionViewModel(ProtocolDescriptor descriptor, SessionServices dependencies)
        : base(descriptor, dependencies)
    {
        SubscribeCommand = new AsyncRelayCommand(SubscribeAsync, () => IsLinked);
        UnsubscribeCommand = new AsyncRelayCommand(UnsubscribeAsync, () => IsLinked);
        PublishCommand = new AsyncRelayCommand(PublishAsync, () => IsLinked);
        AddUserPropertyCommand = new RelayCommand(AddUserProperty);
        RemoveUserPropertyCommand = new RelayCommand<MqttUserPropertyRow?>(RemoveUserProperty);
        ClearReceivedCommand = new RelayCommand(ReceivedMessages.Clear);
        PropertyChanged += OnOwnPropertyChanged;
    }

    /// <summary>订阅列表：每项是一个已订阅的过滤器，显示请求与 Broker 授予的 QoS。</summary>
    public ObservableCollection<MqttSubscriptionRow> Subscriptions { get; } = [];

    /// <summary>接收列表，最新的在最前。</summary>
    public ObservableCollection<MqttReceivedRow> ReceivedMessages { get; } = [];

    /// <summary>发布区的 MQTT 5 用户属性。</summary>
    public ObservableCollection<MqttUserPropertyRow> PublishUserProperties { get; } = [];

    public IReadOnlyList<MqttQos> QosChoices { get; } = Enum.GetValues<MqttQos>();

    public IReadOnlyList<MqttProtocolVersion> VersionChoices { get; } = Enum.GetValues<MqttProtocolVersion>();

    /// <summary>已连接；只有这时订阅、退订与发布才会发出（重连期间库返回 NotConnected）。</summary>
    public bool IsLinked => State == SessionState.Connected;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsVersion311), nameof(IsVersion500))]
    private MqttProtocolVersion _protocolVersion = MqttProtocolVersion.V311;

    public bool IsVersion311 => ProtocolVersion == MqttProtocolVersion.V311;

    public bool IsVersion500 => ProtocolVersion == MqttProtocolVersion.V500;

    [ObservableProperty]
    private string _host = "127.0.0.1";

    [ObservableProperty]
    private int _port = 1883;

    [ObservableProperty]
    private string _clientId = string.Empty;

    [ObservableProperty]
    private string _userName = string.Empty;

    /// <summary>密码只保存在内存中，不写入工作区。</summary>
    [ObservableProperty]
    private string _password = string.Empty;

    [ObservableProperty]
    private bool _cleanSession = true;

    /// <summary>会话过期间隔（秒），只在 V5.0 上有效。</summary>
    [ObservableProperty]
    private uint _sessionExpiryInterval;

    [ObservableProperty]
    private int _keepAliveSeconds = 15;

    /// <summary>连接与确认的超时，毫秒。</summary>
    [ObservableProperty]
    private int _timeout = 10000;

    [ObservableProperty]
    private bool _useTls;

    [ObservableProperty]
    private bool _allowUntrustedCertificates;

    [ObservableProperty]
    private bool _autoReconnect = true;

    [ObservableProperty]
    private int _reconnectInterval = 1000;

    [ObservableProperty]
    private int _maxReconnectInterval = 30000;

    [ObservableProperty]
    private string _willTopic = string.Empty;

    [ObservableProperty]
    private string _willPayload = string.Empty;

    [ObservableProperty]
    private MqttQos _willQos = MqttQos.AtMostOnce;

    [ObservableProperty]
    private bool _willRetain;

    /// <summary>库的连接状态的中文名称（未连接、连接中、已连接、重连中）。</summary>
    [ObservableProperty]
    private string _mqttStateText = MqttSupport.StateName(MqttConnectionState.Disconnected);

    /// <summary>最近一次状态变化的原因（库给出，没有时为空）。</summary>
    [ObservableProperty]
    private string _stateReason = string.Empty;

    [ObservableProperty]
    private string _newTopicFilter = string.Empty;

    [ObservableProperty]
    private MqttQos _newSubscribeQos = MqttQos.AtLeastOnce;

    [ObservableProperty]
    private MqttSubscriptionRow? _selectedSubscription;

    [ObservableProperty]
    private string _subscriptionMessage = string.Empty;

    [ObservableProperty]
    private string _publishTopic = string.Empty;

    [ObservableProperty]
    private string _publishPayload = string.Empty;

    [ObservableProperty]
    private bool _publishPayloadAsHex;

    [ObservableProperty]
    private MqttQos _publishQos = MqttQos.AtMostOnce;

    [ObservableProperty]
    private bool _publishRetain;

    [ObservableProperty]
    private string _contentType = string.Empty;

    [ObservableProperty]
    private string _responseTopic = string.Empty;

    /// <summary>CorrelationData 的十六进制文字，空白表示没有。</summary>
    [ObservableProperty]
    private string _correlationDataHex = string.Empty;

    /// <summary>消息过期间隔（秒）的文字，空白表示不设置。</summary>
    [ObservableProperty]
    private string _messageExpiryText = string.Empty;

    [ObservableProperty]
    private string _newUserPropertyName = string.Empty;

    [ObservableProperty]
    private string _newUserPropertyValue = string.Empty;

    [ObservableProperty]
    private MqttUserPropertyRow? _selectedUserProperty;

    /// <summary>发布区的说明：校验失败、未连接，或发布的结果（库的失败消息）。</summary>
    [ObservableProperty]
    private string _publishMessage = string.Empty;

    public IAsyncRelayCommand SubscribeCommand { get; }

    public IAsyncRelayCommand UnsubscribeCommand { get; }

    public IAsyncRelayCommand PublishCommand { get; }

    public IRelayCommand AddUserPropertyCommand { get; }

    public IRelayCommand<MqttUserPropertyRow?> RemoveUserPropertyCommand { get; }

    public IRelayCommand ClearReceivedCommand { get; }

    public override SessionSettings CaptureSettings() => Capture();

    public override void ApplySettings(SessionSettings settings)
    {
        if (settings is not MqttSettings saved)
        {
            throw new ArgumentException("The settings do not belong to an MQTT page.", nameof(settings));
        }

        Host = saved.Host;
        Port = saved.Port;
        ClientId = saved.ClientId ?? string.Empty;
        UserName = saved.UserName ?? string.Empty;
        ProtocolVersion = saved.ProtocolVersion;
        CleanSession = saved.CleanSession;
        SessionExpiryInterval = saved.SessionExpiryInterval;
        KeepAliveSeconds = saved.KeepAliveSeconds;
        Timeout = saved.TimeoutMilliseconds;
        UseTls = saved.UseTls;
        AllowUntrustedCertificates = saved.AllowUntrustedCertificates;
        AutoReconnect = saved.AutoReconnect;
        ReconnectInterval = saved.ReconnectInterval;
        MaxReconnectInterval = saved.MaxReconnectInterval;
        WillTopic = saved.WillTopic ?? string.Empty;
        WillPayload = saved.WillPayload ?? string.Empty;
        WillQos = saved.WillQos;
        WillRetain = saved.WillRetain;
    }

    protected override async Task ConnectCoreAsync(CancellationToken cancellationToken)
    {
        // 上一个客户端可能是故障后留下的（自动重连关闭）：连接前先释放它。
        await ReleaseClientAsync().ConfigureAwait(true);
        MqttClient client = CreateClient(Capture());
        _client = client;
        client.StateChanged += (_, e) => PostToUi(() =>
        {
            if (ReferenceEquals(_client, client))
            {
                ApplyState(e);
            }
        });
        client.MessageReceived += (_, e) =>
        {
            MqttMessage message = e.Message;
            PostToUi(() =>
            {
                if (ReferenceEquals(_client, client))
                {
                    RecordReceived(message);
                }
            });
        };

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
        MqttStateText = MqttSupport.StateName(MqttConnectionState.Connected);
    }

    protected override async Task DisconnectCoreAsync(CancellationToken cancellationToken)
    {
        if (_client is not { } client)
        {
            ResetState();
            return;
        }

        _client = null;
        Subscriptions.Clear();
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

    private MqttSettings Capture() => new(
        Host.Trim(),
        Port,
        Optional(ClientId),
        Optional(UserName),
        ProtocolVersion,
        CleanSession,
        SessionExpiryInterval,
        KeepAliveSeconds,
        Timeout,
        UseTls,
        AllowUntrustedCertificates,
        AutoReconnect,
        ReconnectInterval,
        MaxReconnectInterval,
        Optional(WillTopic),
        WillPayload.Length == 0 ? null : WillPayload,
        WillQos,
        WillRetain);

    private static string? Optional(string text) => string.IsNullOrWhiteSpace(text) ? null : text.Trim();

    // 主机为空先在页面上拒绝；库的其它选项校验失败时抛出 ArgumentException，这里转成请求无效，与其它页签一致。
    private MqttClient CreateClient(MqttSettings settings)
    {
        if (settings.Host.Length == 0)
        {
            throw new CommFailureException(CommResult.Fail("主机地址不能为空。", CommErrorKind.InvalidRequest));
        }

        var options = new MqttClientOptions
        {
            Host = settings.Host,
            Port = settings.Port,
            ClientId = settings.ClientId,
            UserName = settings.UserName,
            Password = Password.Length == 0 ? null : Password,
            ProtocolVersion = settings.ProtocolVersion,
            CleanSession = settings.CleanSession,
            SessionExpiryInterval = settings.SessionExpiryInterval,
            KeepAliveSeconds = settings.KeepAliveSeconds,
            Timeout = settings.TimeoutMilliseconds,
            UseTls = settings.UseTls,
            AllowUntrustedCertificates = settings.AllowUntrustedCertificates,
            AutoReconnect = settings.AutoReconnect,
            ReconnectInterval = settings.ReconnectInterval,
            MaxReconnectInterval = settings.MaxReconnectInterval,
            WillTopic = settings.WillTopic,
            WillPayload = settings.WillPayload,
            WillQos = settings.WillQos,
            WillRetain = settings.WillRetain,
        };

        try
        {
            return MqttClient.Create(options, SessionLoggerFactory);
        }
        catch (ArgumentException ex)
        {
            throw new CommFailureException(CommResult.Fail(ex.Message, CommErrorKind.InvalidRequest));
        }
    }

    // 释放当前客户端（或指定的那一个）；之后它的事件被忽略，订阅列表随之清空（新连接需要重新订阅）。
    private async Task ReleaseClientAsync(MqttClient? expected = null)
    {
        if (_client is not { } client || (expected is not null && !ReferenceEquals(client, expected)))
        {
            return;
        }

        _client = null;
        Subscriptions.Clear();
        await client.DisposeAsync().ConfigureAwait(true);
    }

    private void ApplyState(MqttConnectionStateChangedEventArgs e)
    {
        MqttStateText = MqttSupport.StateName(e.Current);
        StateReason = e.Reason ?? string.Empty;
        State = MqttSupport.ToSessionState(e.Current, e.Reason);
        if (e.Current == MqttConnectionState.Disconnected)
        {
            // 断开后客户端不再保留订阅；下一次连接会创建新的客户端，因此订阅列表要清空。
            Subscriptions.Clear();
        }
    }

    private void ResetState()
    {
        State = SessionState.NotConnected;
        MqttStateText = MqttSupport.StateName(MqttConnectionState.Disconnected);
        StateReason = string.Empty;
    }

    private void RecordReceived(MqttMessage message)
    {
        var row = new MqttReceivedRow(
            Dependencies.Clock.GetLocalNow().ToString("HH:mm:ss.fff", CultureInfo.InvariantCulture),
            message.Topic,
            MqttSupport.QosText(message.Qos),
            message.Retain ? "是" : "否",
            Encoding.UTF8.GetString(message.Payload.Span),
            MqttSupport.HexText(message.Payload.Span),
            MqttSupport.DescribeProperties(message));
        ReceivedMessages.InsertNewest(row, MaxReceivedMessages);
    }

    private async Task SubscribeAsync()
    {
        if (_client is not { } client)
        {
            SubscriptionMessage = NotConnectedMessage;
            return;
        }

        string filter = NewTopicFilter.Trim();
        if (filter.Length == 0)
        {
            SubscriptionMessage = "主题过滤器不能为空。";
            return;
        }

        MqttQos requested = NewSubscribeQos;
        DateTimeOffset started = Dependencies.Clock.GetLocalNow();
        Stopwatch clock = Stopwatch.StartNew();
        CommResult<MqttQos> result = await client.SubscribeAsync(filter, requested).ConfigureAwait(true);
        RecordOperation(OperationRecords.From(started, "订阅", filter, clock.Elapsed, result));
        if (!ReferenceEquals(_client, client))
        {
            return;
        }

        if (!result.IsSuccess)
        {
            SubscriptionMessage = ResultText.Failure(result.ErrorKind, result.ErrorMessage);
            return;
        }

        MqttQos granted = result.Data;
        MqttSubscriptionRow? existing = Subscriptions.FirstOrDefault(row => row.TopicFilter == filter);
        if (existing is not null)
        {
            Subscriptions.Remove(existing);
        }

        Subscriptions.Add(new MqttSubscriptionRow(filter, requested, granted));
        SubscriptionMessage = granted < requested
            ? $"已订阅 {filter}：Broker 只授予 {MqttSupport.QosText(granted)}，低于请求的 {MqttSupport.QosText(requested)}。"
            : $"已订阅 {filter}，授予 {MqttSupport.QosText(granted)}。";
    }

    private async Task UnsubscribeAsync()
    {
        if (_client is not { } client)
        {
            SubscriptionMessage = NotConnectedMessage;
            return;
        }

        if (SelectedSubscription is not { } row)
        {
            SubscriptionMessage = "请先在订阅列表中选择要退订的过滤器。";
            return;
        }

        DateTimeOffset started = Dependencies.Clock.GetLocalNow();
        Stopwatch clock = Stopwatch.StartNew();
        CommResult result = await client.UnsubscribeAsync(row.TopicFilter).ConfigureAwait(true);
        RecordOperation(OperationRecords.From(started, "退订", row.TopicFilter, clock.Elapsed, result));
        if (!ReferenceEquals(_client, client))
        {
            return;
        }

        if (!result.IsSuccess)
        {
            SubscriptionMessage = ResultText.Failure(result.ErrorKind, result.ErrorMessage);
            return;
        }

        Subscriptions.Remove(row);
        SelectedSubscription = null;
        SubscriptionMessage = $"已退订 {row.TopicFilter}。";
    }

    private async Task PublishAsync()
    {
        if (_client is not { } client)
        {
            PublishMessage = NotConnectedMessage;
            return;
        }

        MqttMessage message;
        try
        {
            message = BuildPublishMessage();
        }
        catch (FormatException ex)
        {
            PublishMessage = $"发布内容无效：{ex.Message}";
            return;
        }

        DateTimeOffset started = Dependencies.Clock.GetLocalNow();
        Stopwatch clock = Stopwatch.StartNew();
        CommResult result = await client.PublishAsync(message).ConfigureAwait(true);
        RecordOperation(OperationRecords.From(started, "发布", message.Topic, clock.Elapsed, result));
        PublishMessage = result.IsSuccess
            ? $"已发布 {message}。"
            : ResultText.Failure(result.ErrorKind, result.ErrorMessage);
    }

    // 输入在发送前全部解析；任何一项格式错误都不发送（FormatException 由调用方显示）。
    private MqttMessage BuildPublishMessage()
    {
        string topic = PublishTopic.Trim();
        if (topic.Length == 0)
        {
            throw new FormatException("发布主题不能为空。");
        }

        return MqttSupport.Build(
            topic,
            MqttSupport.ParsePayload(PublishPayload, PublishPayloadAsHex),
            PublishQos,
            PublishRetain,
            ContentType.Trim(),
            ResponseTopic.Trim(),
            MqttSupport.ParseOptionalHex(CorrelationDataHex),
            MqttSupport.ParseExpiry(MessageExpiryText),
            PublishUserProperties.Select(row => new MqttUserProperty(row.Name, row.Value)).ToArray());
    }

    private void AddUserProperty()
    {
        string name = NewUserPropertyName.Trim();
        if (name.Length == 0)
        {
            PublishMessage = "用户属性的名称不能为空。";
            return;
        }

        PublishUserProperties.Add(new MqttUserPropertyRow(name, NewUserPropertyValue));
        NewUserPropertyName = string.Empty;
        NewUserPropertyValue = string.Empty;
    }

    private void RemoveUserProperty(MqttUserPropertyRow? row)
    {
        if (row is not null)
        {
            PublishUserProperties.Remove(row);
        }
    }

    private void OnOwnPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(State))
        {
            return;
        }

        OnPropertyChanged(nameof(IsLinked));
        SubscribeCommand.NotifyCanExecuteChanged();
        UnsubscribeCommand.NotifyCanExecuteChanged();
        PublishCommand.NotifyCanExecuteChanged();
    }
}
