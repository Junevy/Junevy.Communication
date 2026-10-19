using System.ComponentModel.DataAnnotations;
using CommunityToolkit.Mvvm.ComponentModel;
using Junevy.Communication.Channels;

namespace Junevy.Communication.DebugAssistant.Protocols.Channels.Editors;

/// <summary>心跳编辑器（计划第 1 节）。映射到 <see cref="HeartbeatOptions"/>。</summary>
public sealed partial class HeartbeatEditor : ValidatedEditor
{
    [ObservableProperty]
    private bool _enabled;

    [ObservableProperty]
    [Range(100, 3600000, ErrorMessage = "心跳间隔必须在 100 到 3600000 毫秒之间。")]
    private int _interval = 5000;

    [ObservableProperty]
    [Range(1, 600000, ErrorMessage = "心跳超时必须在 1 到 600000 毫秒之间。")]
    private int _timeout = 2000;

    [ObservableProperty]
    [Range(1, 100, ErrorMessage = "连续失败次数必须在 1 到 100 之间。")]
    private int _maxFailures = 3;

    [ObservableProperty]
    private bool _onlyWhenIdle = true;

    /// <summary>心跳发送的内容（<c>hex:</c> 或文本语法）；串口与 UDP 必须同时填写期待的应答。</summary>
    [ObservableProperty]
    private string _payload = "hex:00";

    [ObservableProperty]
    private string _expectedReply = string.Empty;

    public HeartbeatOptions ToOptions() => new()
    {
        Enabled = Enabled,
        Interval = Interval,
        Timeout = Timeout,
        MaxFailures = MaxFailures,
        OnlyWhenIdle = OnlyWhenIdle,
        Payload = Enabled ? Payload : null,
        ExpectedReply = Enabled && ExpectedReply.Length > 0 ? ExpectedReply : null,
    };
}

/// <summary>重连编辑器（计划第 1 节）。映射到 <see cref="ReconnectOptions"/>。</summary>
public sealed partial class ReconnectEditor : ValidatedEditor
{
    [ObservableProperty]
    private bool _enabled;

    [ObservableProperty]
    private ReconnectMode _mode = ReconnectMode.ExponentialBackoff;

    [ObservableProperty]
    [Range(1, 3600000, ErrorMessage = "重连间隔必须在 1 到 3600000 毫秒之间。")]
    private int _interval = 1000;

    [ObservableProperty]
    [Range(1, 3600000, ErrorMessage = "最大重连间隔必须在 1 到 3600000 毫秒之间。")]
    private int _maxInterval = 30000;

    /// <summary>最多尝试次数，0 表示不限。</summary>
    [ObservableProperty]
    [Range(0, 100000, ErrorMessage = "重连次数必须在 0 到 100000 之间（0 表示不限）。")]
    private int _maxAttempts;

    /// <summary>首次连接失败后是否继续重连。</summary>
    [ObservableProperty]
    private bool _onInitialFailure;

    public ReconnectOptions ToOptions() => new()
    {
        Enabled = Enabled,
        Mode = Mode,
        Interval = Interval,
        MaxInterval = MaxInterval,
        MaxAttempts = MaxAttempts,
        OnInitialFailure = OnInitialFailure,
    };
}
