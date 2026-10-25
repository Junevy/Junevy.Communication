using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Junevy.Communication.Core.Results;
using Junevy.Communication.DebugAssistant.Common;
using Junevy.Communication.DebugAssistant.Sessions;
using Junevy.Communication.DebugAssistant.Shell;
using Junevy.Communication.DebugAssistant.Workspace;
using Junevy.Communication.Secs;
using Junevy.Communication.Secs.Hsms;

namespace Junevy.Communication.DebugAssistant.Protocols.Secs;

/// <summary>
/// SECS/HSMS 设备端页（计划第 7.6 节）：启动与停止设备，会话状态与原因，自动应答规则（启动后的修改立即生效），
/// 收到的主消息（只有有规则的 S/F 显示正文，F-4），发送主消息（SML 编辑、模板、W 位）与结果。
/// 每次启动都创建新的库设备端（与 TCP 服务端页一致），因此停止后可以再次启动，并使用新的参数。
/// </summary>
public sealed partial class SecsSessionViewModel : SessionViewModel
{
    private const int MaxReceivedMessages = 200;

    private HsmsEquipment? _equipment;

    public SecsSessionViewModel(ProtocolDescriptor descriptor, SessionServices dependencies)
        : base(descriptor, dependencies)
    {
        SendCommand = new AsyncRelayCommand(SendPrimaryAsync);
        AddRuleCommand = new RelayCommand(AddRule);
        RemoveRuleCommand = new RelayCommand<SecsRule?>(RemoveRule);
        ApplyTemplateCommand = new RelayCommand(ApplyTemplate);
    }

    /// <summary>自动应答规则，按 S、F 唯一。</summary>
    public ObservableCollection<SecsRule> Rules { get; } = [];

    /// <summary>收到的主消息（有规则的 S/F），最新的在最前。</summary>
    public ObservableCollection<SecsReceivedMessage> ReceivedMessages { get; } = [];

    /// <summary>发送区的常用模板。</summary>
    public IReadOnlyList<SecsTemplate> Templates => SecsSupport.Templates;

    [ObservableProperty]
    private HsmsConnectionMode _mode = HsmsConnectionMode.Passive;

    /// <summary>被动：监听地址，留空表示所有地址。主动：主机地址（必填）。</summary>
    [ObservableProperty]
    private string _host = string.Empty;

    [ObservableProperty]
    private int _port = 5000;

    [ObservableProperty]
    private int _deviceId;

    [ObservableProperty]
    private int _t3 = 45000;

    [ObservableProperty]
    private int _t5 = 10000;

    [ObservableProperty]
    private int _t6 = 5000;

    [ObservableProperty]
    private int _t7 = 10000;

    [ObservableProperty]
    private int _t8 = 5000;

    [ObservableProperty]
    private int _linktestInterval;

    [ObservableProperty]
    private int _maxMessageLength = 16 * 1024 * 1024;

    /// <summary>会话状态的中文名称（未连接、未选择、已选择）。</summary>
    [ObservableProperty]
    private string _hsmsStateText = SecsSupport.StateName(HsmsState.NotConnected);

    /// <summary>最近一次状态变化的原因（库给出）。</summary>
    [ObservableProperty]
    private string _stateReason = string.Empty;

    [ObservableProperty]
    private int _newRuleStream = 1;

    [ObservableProperty]
    private int _newRuleFunction = 1;

    [ObservableProperty]
    private SecsRuleKind _newRuleKind = SecsRuleKind.Reply;

    [ObservableProperty]
    private string _newRuleSml = string.Empty;

    [ObservableProperty]
    private string _ruleMessage = string.Empty;

    [ObservableProperty]
    private int _sendStream = 5;

    [ObservableProperty]
    private int _sendFunction = 1;

    [ObservableProperty]
    private bool _sendReplyExpected = true;

    [ObservableProperty]
    private string _sendSml = string.Empty;

    [ObservableProperty]
    private SecsTemplate? _selectedTemplate;

    /// <summary>发送区的说明（校验失败、未运行、或发送结果）。</summary>
    [ObservableProperty]
    private string _sendMessage = string.Empty;

    /// <summary>收到的应答的 SML；没有应答时为空。</summary>
    [ObservableProperty]
    private string _replySml = string.Empty;

    public IAsyncRelayCommand SendCommand { get; }

    public IRelayCommand AddRuleCommand { get; }

    public IRelayCommand<SecsRule?> RemoveRuleCommand { get; }

    public IRelayCommand ApplyTemplateCommand { get; }

    public override string ConnectText => "启动";

    public override string DisconnectText => "停止";

    public override SessionSettings CaptureSettings() => Capture();

    public override void ApplySettings(SessionSettings settings)
    {
        if (settings is not SecsSettings saved)
        {
            throw new ArgumentException("The settings do not belong to a SECS page.", nameof(settings));
        }

        foreach (SecsRule rule in saved.Rules)
        {
            if (SecsSupport.RuleProblem(rule.Stream, rule.Function, rule.Kind, rule.Sml, out _) is { } problem)
            {
                throw new ArgumentException($"S{rule.Stream}F{rule.Function}：{problem}", nameof(settings));
            }
        }

        Mode = saved.Mode;
        Host = saved.Host ?? string.Empty;
        Port = saved.Port;
        DeviceId = saved.DeviceId;
        T3 = saved.T3;
        T5 = saved.T5;
        T6 = saved.T6;
        T7 = saved.T7;
        T8 = saved.T8;
        LinktestInterval = saved.LinktestInterval;
        MaxMessageLength = saved.MaxMessageLength;
        Rules.Clear();
        foreach (SecsRule rule in saved.Rules)
        {
            Rules.Add(rule);
        }
    }

    protected override async Task ConnectCoreAsync(CancellationToken cancellationToken)
    {
        SecsSettings settings = Capture();
        if (SecsSupport.SettingsProblem(settings) is { } problem)
        {
            throw new CommFailureException(CommResult.Fail(problem, CommErrorKind.InvalidRequest));
        }

        IReadOnlyList<(SecsRule Rule, SecsItem? Body)> rules = CompileRules(settings.Rules);
        HsmsEquipment equipment = CreateEquipment(settings);
        // 事件在线程池上触发，可能在后来的事件之后才被处理：只应用与库当前状态一致的事件，过期的旧事件丢弃（最新状态的事件随后必然到达），与通道页签同一处理（Task 21）。
        equipment.StateChanged += (_, e) => PostToUi(() =>
        {
            if (equipment.State == e.Current)
            {
                OnHsmsStateChanged(e);
            }
        });
        foreach ((SecsRule rule, SecsItem? body) in rules)
        {
            equipment.SetHandler(rule.Stream, rule.Function, CreateHandler(rule.Kind, body));
        }

        CommResult started = await equipment.StartAsync(cancellationToken).ConfigureAwait(true);
        if (!started.IsSuccess)
        {
            await equipment.DisposeAsync().ConfigureAwait(true);
            throw new CommFailureException(started);
        }

        _equipment = equipment;
        State = SessionState.Running;
        HsmsStateText = SecsSupport.StateName(equipment.State);
        StateReason = string.Empty;
    }

    protected override async Task DisconnectCoreAsync(CancellationToken cancellationToken)
    {
        if (_equipment is not { } equipment)
        {
            return;
        }

        _equipment = null;
        State = SessionState.Stopped;
        HsmsStateText = SecsSupport.StateName(HsmsState.NotConnected);
        await equipment.StopAsync(cancellationToken).ConfigureAwait(true);
        await equipment.DisposeAsync().ConfigureAwait(true);
    }

    private SecsSettings Capture() => new(
        Mode,
        string.IsNullOrWhiteSpace(Host) ? null : Host.Trim(),
        Port,
        DeviceId,
        T3,
        T5,
        T6,
        T7,
        T8,
        LinktestInterval,
        MaxMessageLength,
        [.. Rules]);

    // 规则在启动前全部编译：说明不合法的规则让启动失败，而不是静默丢弃。
    private static IReadOnlyList<(SecsRule Rule, SecsItem? Body)> CompileRules(IEnumerable<SecsRule> rules)
    {
        var compiled = new List<(SecsRule Rule, SecsItem? Body)>();
        foreach (SecsRule rule in rules)
        {
            if (SecsSupport.RuleProblem(rule.Stream, rule.Function, rule.Kind, rule.Sml, out SecsItem? body) is { } problem)
            {
                throw new CommFailureException(CommResult.Fail($"S{rule.Stream}F{rule.Function}：{problem}", CommErrorKind.InvalidRequest));
            }

            compiled.Add((rule, body));
        }

        return compiled;
    }

    private HsmsEquipment CreateEquipment(SecsSettings settings)
    {
        var options = new HsmsEquipmentOptions
        {
            Mode = settings.Mode,
            Host = settings.Host,
            Port = settings.Port,
            DeviceId = (ushort)settings.DeviceId,
            T3 = settings.T3,
            T5 = settings.T5,
            T6 = settings.T6,
            T7 = settings.T7,
            T8 = settings.T8,
            LinktestInterval = settings.LinktestInterval,
            MaxMessageLength = settings.MaxMessageLength,
        };
        try
        {
            return HsmsEquipment.Create(options, SessionLoggerFactory);
        }
        catch (ArgumentException ex)
        {
            throw new CommFailureException(CommResult.Fail(ex.Message, CommErrorKind.InvalidRequest));
        }
    }

    // 处理器只记录收到的主消息并给出应答；没有 W 位的主消息不应答（库会忽略返回值，并对非空返回值记警告）。
    private SecsMessageHandler CreateHandler(SecsRuleKind kind, SecsItem? body) => (primary, _) =>
    {
        RecordReceived(primary);
        SecsMessage? reply = kind == SecsRuleKind.Reply && primary.ReplyExpected ? primary.CreateReply(body) : null;
        return Task.FromResult(reply);
    };

    private void RecordReceived(SecsMessage primary)
    {
        var row = new SecsReceivedMessage(
            DateTimeOffset.Now.ToString("HH:mm:ss.fff", CultureInfo.InvariantCulture),
            $"S{primary.Stream}F{primary.Function}{(primary.ReplyExpected ? " W" : string.Empty)}",
            primary.ToSml());
        PostToUi(() => ReceivedMessages.InsertNewest(row, MaxReceivedMessages));
    }

    private void OnHsmsStateChanged(HsmsStateChangedEventArgs e)
    {
        HsmsStateText = SecsSupport.StateName(e.Current);
        StateReason = e.Reason ?? string.Empty;
    }

    // 运行中修改规则立即生效：库的处理器表按 S/F 替换或删除。
    private void AddRule()
    {
        if (SecsSupport.RuleProblem(NewRuleStream, NewRuleFunction, NewRuleKind, NewRuleSml, out SecsItem? body) is { } problem)
        {
            RuleMessage = problem;
            return;
        }

        var rule = new SecsRule((byte)NewRuleStream, (byte)NewRuleFunction, NewRuleKind, NewRuleSml);
        foreach (SecsRule replaced in Rules.Where(candidate => candidate.Stream == rule.Stream && candidate.Function == rule.Function).ToList())
        {
            Rules.Remove(replaced);
        }

        Rules.Add(rule);
        _equipment?.SetHandler(rule.Stream, rule.Function, CreateHandler(rule.Kind, body));
        RuleMessage = $"已登记 S{rule.Stream}F{rule.Function}。";
    }

    private void RemoveRule(SecsRule? rule)
    {
        if (rule is null)
        {
            return;
        }

        Rules.Remove(rule);
        _equipment?.RemoveHandler(rule.Stream, rule.Function);
        RuleMessage = $"已删除 S{rule.Stream}F{rule.Function}。";
    }

    private void ApplyTemplate()
    {
        if (SelectedTemplate is not { } template)
        {
            return;
        }

        SendStream = template.Stream;
        SendFunction = template.Function;
        SendReplyExpected = template.ReplyExpected;
        SendSml = template.Sml;
    }

    private async Task SendPrimaryAsync()
    {
        if (_equipment is not { } equipment)
        {
            SendMessage = "页签没有运行，请先启动。";
            return;
        }

        if (SecsSupport.SendProblem(SendStream, SendFunction, SendSml, out SecsItem? body) is { } problem)
        {
            SendMessage = problem;
            return;
        }

        var message = SecsMessage.Create((byte)SendStream, (byte)SendFunction, SendReplyExpected, body);
        DateTimeOffset startedAt = DateTimeOffset.Now;
        long started = Stopwatch.GetTimestamp();
        SecsResult result = await equipment.SendAsync(message).ConfigureAwait(true);
        RecordOperation(OperationRecords.From(startedAt, "发送", $"S{SendStream}F{SendFunction}", Stopwatch.GetElapsedTime(started), result));
        ReplySml = result.Reply?.ToSml() ?? string.Empty;
        SendMessage = SecsSupport.Describe(result);
    }
}
