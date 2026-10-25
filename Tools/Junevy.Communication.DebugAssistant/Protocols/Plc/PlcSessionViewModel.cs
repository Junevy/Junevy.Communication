using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Junevy.Communication.Channels;
using Junevy.Communication.Core.Buffers;
using Junevy.Communication.Core.Results;
using Junevy.Communication.DebugAssistant.Common;
using Junevy.Communication.DebugAssistant.Protocols.Channels.Editors;
using Junevy.Communication.DebugAssistant.Protocols.Channels.Settings;
using Junevy.Communication.DebugAssistant.Sessions;
using Junevy.Communication.DebugAssistant.Shell;
using Junevy.Communication.DebugAssistant.Workspace;
using Junevy.Communication.Plc;

namespace Junevy.Communication.DebugAssistant.Protocols.Plc;

/// <summary>
/// PLC 页签的共同部分（计划第 7.1 节）：连接与断开、地址实时校验、类型化读写、监控表与套件选项。
/// 协议页签只提供创建客户端（<see cref="CreateClient"/>）与协议特有的区域；公共面板只面向 <see cref="IPlcClient"/>。
/// </summary>
public abstract partial class PlcSessionViewModel : SessionViewModel
{
    private const int MinMonitorIntervalMs = 50;
    private const int MaxMonitorIntervalMs = 3600000;
    private const int MaxCount = 1000;

    private readonly PlcPoller _poller;
    private IPlcClient? _client;

    /// <summary>监控行的不可变快照：界面线程替换整个数组，轮询线程只读取。</summary>
    private volatile PlcMonitorRow[] _rows = [];

    protected PlcSessionViewModel(ProtocolDescriptor descriptor, SessionServices dependencies, ChannelSettingsEditor channel, WordOrder protocolWordOrder)
        : base(descriptor, dependencies)
    {
        Channel = channel;
        Options = new PlcOptionsEditor(protocolWordOrder);
        _poller = new PlcPoller(RoundAsync);
        Monitor.CollectionChanged += (_, _) => _rows = [.. Monitor];
        ReadCommand = new AsyncRelayCommand(ReadOnceAsync);
        WriteCommand = new AsyncRelayCommand(WriteOnceAsync);
        AddMonitorCommand = new RelayCommand(AddMonitorRow);
        RemoveMonitorCommand = new RelayCommand<PlcMonitorRow?>(RemoveMonitorRow);
        WriteMonitorCommand = new AsyncRelayCommand<PlcMonitorRow?>(WriteMonitorRowAsync);
        StartMonitorCommand = new RelayCommand(StartMonitor);
        StopMonitorCommand = new RelayCommand(StopMonitor);
    }

    /// <summary>通道参数：超时、心跳、重连、套接字与 TLS。分帧与关联由协议决定，不显示。</summary>
    public ChannelSettingsEditor Channel { get; }

    /// <summary>套件选项：重试、读改写位，以及字序与字符串编码的覆盖。</summary>
    public PlcOptionsEditor Options { get; }

    /// <summary>监控表。</summary>
    public ObservableCollection<PlcMonitorRow> Monitor { get; } = [];

    /// <summary>地址（协议的写法）。输入时实时校验，不发报文。</summary>
    [ObservableProperty]
    private string _address = string.Empty;

    /// <summary>地址校验的结果：解析后的存储区、单位与编号，或解析器的消息。</summary>
    [ObservableProperty]
    private string _addressCheck = "请输入地址。";

    [ObservableProperty]
    private PlcValueType _valueType = PlcValueType.Int16;

    /// <summary>数量或长度（字符串为字节长度，写入时作为定长），含义见 <see cref="PlcValueType"/>。</summary>
    [ObservableProperty]
    private int _count = 1;

    /// <summary>要写入的值，逗号分隔。</summary>
    [ObservableProperty]
    private string _writeText = string.Empty;

    /// <summary>最近一次成功读取的值。</summary>
    [ObservableProperty]
    private string _readText = string.Empty;

    [ObservableProperty]
    private bool _isMonitoring;

    /// <summary>监控表的轮询间隔（毫秒），50 到 3600000。</summary>
    [ObservableProperty]
    private int _monitorIntervalMs = 500;

    public IAsyncRelayCommand ReadCommand { get; }

    public IAsyncRelayCommand WriteCommand { get; }

    public IRelayCommand AddMonitorCommand { get; }

    public IRelayCommand<PlcMonitorRow?> RemoveMonitorCommand { get; }

    public IAsyncRelayCommand<PlcMonitorRow?> WriteMonitorCommand { get; }

    public IRelayCommand StartMonitorCommand { get; }

    public IRelayCommand StopMonitorCommand { get; }

    /// <summary>创建协议的客户端（未连接）。参数不合法时抛出 <see cref="ArgumentException"/>。</summary>
    protected abstract IPlcClient CreateClient();

    /// <summary>当前连接的客户端；未连接时为 null。协议页签用它执行协议特有的操作（例如 Modbus 的掩码写）。</summary>
    protected IPlcClient? CurrentClient => _client;

    /// <summary>把面板上的地址换成协议实际使用的地址（例如 Modbus 的临时站号前缀）。默认不变。</summary>
    protected virtual string ToProtocolAddress(string address) => address;

    /// <summary>描述解析后的地址。协议页签可以覆盖它，补充附加信息（例如 Modbus 的站号）。</summary>
    protected virtual string DescribeAddress(PlcAddress address) =>
        $"存储区 {address.Device.Symbol}，单位 {address.Unit}，编号 {address.Number}"
        + (address.BitIndex is { } bit ? $"，位 {bit}" : string.Empty) + "。";

    /// <summary>解析地址文字。已连接时使用当前客户端，否则临时创建一个（不连接，不发报文）。</summary>
    protected virtual CommResult<PlcAddress> ParseAddressText(string text)
    {
        if (_client is { } client)
        {
            return client.ParseAddress(text);
        }

        try
        {
            using IPlcClient temporary = CreateClient();
            return temporary.ParseAddress(text);
        }
        catch (ArgumentException ex)
        {
            return CommResult<PlcAddress>.Fail(ex.Message, CommErrorKind.InvalidRequest);
        }
    }

    /// <summary>公共面板的参数：通道、套件选项、地址与类型、监控表。协议页签把它放进自己的参数里。</summary>
    protected PlcPanelSettings CapturePanel() => new(
        Channel.Capture(),
        EditorSnapshot.Capture(Options),
        Address,
        ValueType,
        Count,
        MonitorIntervalMs,
        Monitor.Select(row => new PlcMonitorSetting(row.Address, row.Type, row.Count)).ToArray());

    /// <summary>恢复公共面板的参数；监控表按保存的顺序重建。</summary>
    protected void ApplyPanel(PlcPanelSettings saved)
    {
        Channel.Apply(saved.Channel);
        EditorSnapshot.Apply(Options, saved.Options);
        Address = saved.Address;
        ValueType = saved.ValueType;
        Count = saved.Count;
        MonitorIntervalMs = saved.MonitorIntervalMs;
        Monitor.Clear();
        foreach (PlcMonitorSetting row in saved.Monitor)
        {
            Monitor.Add(new PlcMonitorRow(row.Address, row.Type, row.Count));
        }
    }

    protected override async Task ConnectCoreAsync(CancellationToken cancellationToken)
    {
        if (!Channel.IsValid || Options.HasErrors)
        {
            throw new CommFailureException(CommResult.Fail("参数有误，请先修正标红的字段。", CommErrorKind.InvalidRequest));
        }

        IPlcClient client = CreateClientOrFail();
        State = SessionState.Connecting;
        try
        {
            CommResult result = await client.ConnectAsync(cancellationToken);
            if (!result.IsSuccess)
            {
                throw new CommFailureException(result);
            }
        }
        catch
        {
            State = SessionState.NotConnected;
            await client.DisposeAsync();
            throw;
        }

        // 事件在线程池上触发，可能在后来的事件之后才被处理：读取客户端的当前状态，而不是事件里的快照（与通道页签同一处理，Task 21）。
        client.StateChanged += (_, _) => PostToUi(() => OnConnectionStateChanged(client, client.State));
        _client = client;
        State = SessionState.Connected;
    }

    protected override async Task DisconnectCoreAsync(CancellationToken cancellationToken)
    {
        if (_client is not { } client)
        {
            return;
        }

        _client = null;
        StopMonitor();
        State = SessionState.NotConnected;
        try
        {
            await client.DisconnectAsync(cancellationToken);
        }
        finally
        {
            await client.DisposeAsync();
        }
    }

    protected override async ValueTask DisposeCoreAsync()
    {
        try
        {
            await base.DisposeCoreAsync();
        }
        finally
        {
            await _poller.DisposeAsync();
        }
    }

    /// <summary>连接中断时停止监控（DA-D10）；页签状态随库的连接状态变化。</summary>
    private void OnConnectionStateChanged(IPlcClient owner, ConnectionState state)
    {
        if (!ReferenceEquals(owner, _client))
        {
            return;
        }

        State = ConnectionStates.ToSessionState(state);
        if (State is SessionState.NotConnected or SessionState.Reconnecting)
        {
            StopMonitor();
        }
    }

    private async Task ReadOnceAsync()
    {
        if (_client is not { } client)
        {
            RecordNotConnected("读取", Address);
            return;
        }

        if (!TryValidateCount(Count, "读取", Address))
        {
            return;
        }

        DateTimeOffset started = Dependencies.Clock.GetLocalNow();
        (CommResult result, object? value) = await PlcTypedAccess.ReadAsync(client, ValueType, ToProtocolAddress(Address), Count, CancellationToken.None);
        RecordOperation(OperationRecords.From(started, "读取", Address, Since(started), result));
        ReadText = result.IsSuccess ? PlcValueCodec.Format(value) : string.Empty;
    }

    private async Task WriteOnceAsync()
    {
        if (_client is not { } client)
        {
            RecordNotConnected("写入", Address);
            return;
        }

        if (!TryParseValue(ValueType, WriteText, Address, out object? value))
        {
            return;
        }

        DateTimeOffset started = Dependencies.Clock.GetLocalNow();
        CommResult result = await PlcTypedAccess.WriteAsync(client, ValueType, ToProtocolAddress(Address), value!, StringLength(ValueType, Count), CancellationToken.None);
        RecordOperation(OperationRecords.From(started, "写入", Address, Since(started), result));
        if (result.IsSuccess)
        {
            await _poller.RefreshAsync(client);
        }
    }

    private async Task WriteMonitorRowAsync(PlcMonitorRow? row)
    {
        if (row is null)
        {
            return;
        }

        if (_client is not { } client)
        {
            RecordNotConnected("写入", row.Address);
            return;
        }

        if (!TryParseValue(row.Type, row.NewValue, row.Address, out object? value))
        {
            return;
        }

        DateTimeOffset started = Dependencies.Clock.GetLocalNow();
        CommResult result = await PlcTypedAccess.WriteAsync(client, row.Type, ToProtocolAddress(row.Address), value!, StringLength(row.Type, row.Count), CancellationToken.None);
        RecordOperation(OperationRecords.From(started, "写入", row.Address, Since(started), result));
        if (result.IsSuccess)
        {
            row.NewValue = string.Empty;
            await _poller.RefreshAsync(client);
        }
    }

    private void AddMonitorRow()
    {
        if (!TryValidateCount(Count, "添加监控", Address))
        {
            return;
        }

        if (!ParseAddressText(ToProtocolAddress(Address)).IsSuccess)
        {
            RecordOperation(new OperationRecord(Dependencies.Clock.GetLocalNow(), "添加监控", Address, TimeSpan.Zero, false, CommErrorKind.InvalidRequest, null, AddressCheck));
            return;
        }

        Monitor.Add(new PlcMonitorRow(Address.Trim(), ValueType, Count));
    }

    private void RemoveMonitorRow(PlcMonitorRow? row)
    {
        if (row is not null)
        {
            Monitor.Remove(row);
        }
    }

    private void StartMonitor()
    {
        if (IsMonitoring)
        {
            return;
        }

        if (_client is not { } client)
        {
            RecordNotConnected("监控", Address);
            return;
        }

        if (MonitorIntervalMs is < MinMonitorIntervalMs or > MaxMonitorIntervalMs)
        {
            RecordOperation(new OperationRecord(Dependencies.Clock.GetLocalNow(), "监控", Address, TimeSpan.Zero, false, CommErrorKind.InvalidRequest, null,
                "监控间隔必须在 50 到 3600000 毫秒之间。"));
            return;
        }

        _poller.Start(client, MonitorIntervalMs);
        IsMonitoring = true;
    }

    private void StopMonitor()
    {
        _poller.Stop();
        IsMonitoring = false;
    }

    /// <summary>
    /// 一轮轮询：逐行读取并写回界面（经 <see cref="PostToUi"/>）。单行的失败只影响该行的状态，不抛出异常。
    /// </summary>
    private async Task RoundAsync(IPlcClient client, CancellationToken cancellationToken)
    {
        foreach (PlcMonitorRow row in _rows)
        {
            cancellationToken.ThrowIfCancellationRequested();
            CommResult result;
            object? value = null;
            try
            {
                (result, value) = await PlcTypedAccess.ReadAsync(client, row.Type, ToProtocolAddress(row.Address), row.Count, cancellationToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                result = CommResult.Fail(ex.Message, CommErrorKind.Unspecified);
            }

            string text = result.IsSuccess ? PlcValueCodec.Format(value) : string.Empty;
            string status = result.IsSuccess ? "正常" : $"{CommErrorKindNames.Of(result.ErrorKind)}：{result.ErrorMessage}";
            DateTimeOffset now = Dependencies.Clock.GetLocalNow();
            PostToUi(() =>
            {
                if (result.IsSuccess)
                {
                    row.Value = text;
                }

                row.Status = status;
                row.UpdatedAt = now;
            });
        }
    }

    private string CheckAddress(string text)
    {
        if (text.Trim().Length == 0)
        {
            return "请输入地址。";
        }

        CommResult<PlcAddress> parsed = ParseAddressText(ToProtocolAddress(text));
        return parsed.IsSuccess ? DescribeAddress(parsed.Data!) : parsed.ErrorMessage;
    }

    partial void OnAddressChanged(string value) => AddressCheck = CheckAddress(value);

    private IPlcClient CreateClientOrFail()
    {
        try
        {
            return CreateClient();
        }
        catch (ArgumentException ex)
        {
            throw new CommFailureException(CommResult.Fail(ex.Message, CommErrorKind.InvalidRequest));
        }
    }

    private bool TryParseValue(PlcValueType type, string text, string address, out object? value)
    {
        try
        {
            value = PlcValueCodec.Parse(type, text, EncodingChoice.Utf8);
            return true;
        }
        catch (FormatException ex)
        {
            RecordOperation(new OperationRecord(Dependencies.Clock.GetLocalNow(), "写入", address, TimeSpan.Zero, false, CommErrorKind.InvalidRequest, null, ex.Message));
            value = null;
            return false;
        }
    }

    private bool TryValidateCount(int count, string operation, string address)
    {
        if (count is >= 1 and <= MaxCount)
        {
            return true;
        }

        RecordOperation(new OperationRecord(Dependencies.Clock.GetLocalNow(), operation, address, TimeSpan.Zero, false, CommErrorKind.InvalidRequest, null,
            $"数量必须在 1 到 {MaxCount} 之间。"));
        return false;
    }

    /// <summary>记录页签没有连接的失败（协议页签的操作也使用它）。</summary>
    protected void RecordNotConnected(string operation, string address) =>
        RecordOperation(new OperationRecord(Dependencies.Clock.GetLocalNow(), operation, address, TimeSpan.Zero, false, CommErrorKind.NotConnected, null, "页签没有连接。"));

    /// <summary>字符串写入时的定长（字节）；其他类型不使用。</summary>
    private static int StringLength(PlcValueType type, int count) => type == PlcValueType.String ? count : 0;
}

/// <summary>PLC 公共面板的参数（计划 DA-D11）：通道参数、套件选项（<c>options</c> 节的键值），地址与类型，以及监控表。</summary>
public sealed record PlcPanelSettings(
    ChannelSettingsValues Channel,
    Dictionary<string, string> Options,
    string Address,
    PlcValueType ValueType,
    int Count,
    int MonitorIntervalMs,
    IReadOnlyList<PlcMonitorSetting> Monitor);

/// <summary>保存的监控行：地址、类型与数量或长度。</summary>
public sealed record PlcMonitorSetting(string Address, PlcValueType Type, int Count);
