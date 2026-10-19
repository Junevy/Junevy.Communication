using System.Net;
using Junevy.Communication.Secs;
using Junevy.Communication.Secs.Hsms;

namespace Junevy.Communication.DebugAssistant.Protocols.Secs;

/// <summary>
/// SECS 页签的校验、模板与显示文字（计划第 7.6 节）。全部是纯函数，校验说明与库的范围一致（库的英文消息只在最后兜底）。
/// </summary>
public static class SecsSupport
{
    /// <summary>S 的上限（库的 <c>SetHandler</c> 与 HSMS 头的 Stream 位）。</summary>
    public const int MaximumStream = 127;

    /// <summary>F 的上限（一个字节）。</summary>
    public const int MaximumFunction = 255;

    /// <summary>端口的上限。</summary>
    public const int MaximumPort = 65535;

    /// <summary>设备号的上限（库 <c>HsmsEquipmentSettings</c> 的规则）。</summary>
    public const int MaximumDeviceId = 32767;

    /// <summary>报文长度的下限（库的规则）。</summary>
    public const int MinimumMessageLength = 1024;

    /// <summary>设备端的应答模板（常用的主消息，正文为示例值）。</summary>
    public static IReadOnlyList<SecsTemplate> Templates { get; } =
    [
        new("S1F1 W 在线询问", 1, 1, true, string.Empty),
        new("S1F13 W 建立通信", 1, 13, true, "<L [2]\n  <A \"EQ01\">\n  <A \"1.0.0\">\n>"),
        new("S5F1 W 报警报告", 5, 1, true, "<L [2]\n  <B 0x80>\n  <U4 1001>\n>"),
        new("S6F11 W 事件报告", 6, 11, true, "<L [3]\n  <U4 1>\n  <U4 1001>\n  <L [0]>\n>"),
        new("S10F1 W 终端请求", 10, 1, true, "<L [2]\n  <B 0x00>\n  <A \"hello\">\n>"),
    ];

    /// <summary>第一个不合法的设置的说明；全部合法时为 null。</summary>
    public static string? SettingsProblem(SecsSettings settings)
    {
        if (settings.Mode == HsmsConnectionMode.Active && settings.Host is null)
        {
            return "主动模式必须填写主机地址（IP 或主机名）。";
        }

        if (settings.Mode == HsmsConnectionMode.Passive && settings.Host is not null && !IPAddress.TryParse(settings.Host, out _))
        {
            return "被动模式的监听地址必须是 IP 地址；留空表示所有地址。";
        }

        if (settings.Port is < 1 or > MaximumPort)
        {
            return $"端口必须在 1 到 {MaximumPort} 之间。";
        }

        if (settings.DeviceId is < 0 or > MaximumDeviceId)
        {
            return $"设备号必须在 0 到 {MaximumDeviceId} 之间。";
        }

        foreach ((string name, int value) in Timers(settings))
        {
            if (value < 1)
            {
                return $"{name} 必须是正数（毫秒）。";
            }
        }

        if (settings.LinktestInterval < 0)
        {
            return "Linktest 间隔不能为负数（0 表示不发送 Linktest）。";
        }

        return settings.MaxMessageLength < MinimumMessageLength
            ? $"最大报文长度不能小于 {MinimumMessageLength} 字节。"
            : null;
    }

    /// <summary>自动应答规则的说明；合法时为 null，并给出应答用的正文（中止规则没有正文）。</summary>
    public static string? RuleProblem(int stream, int function, SecsRuleKind kind, string sml, out SecsItem? body)
    {
        body = null;
        if (stream is < 0 or > MaximumStream)
        {
            return $"S 必须在 0 到 {MaximumStream} 之间。";
        }

        if (!IsPrimaryFunction(function))
        {
            return "F 必须是 1 到 255 之间的奇数（主消息的功能号）。";
        }

        return kind == SecsRuleKind.Abort ? null : ParseBody(sml, out body);
    }

    /// <summary>发送的主消息的说明；合法时为 null，并给出正文（空白文本表示没有正文）。</summary>
    public static string? SendProblem(int stream, int function, string sml, out SecsItem? body)
    {
        body = null;
        if (stream is < 0 or > MaximumStream)
        {
            return $"S 必须在 0 到 {MaximumStream} 之间。";
        }

        if (!IsPrimaryFunction(function))
        {
            return "F 必须是 1 到 255 之间的奇数；发送的必须是主消息。";
        }

        return ParseBody(sml, out body);
    }

    /// <summary>解析正文的 SML。空白文本表示没有正文（<paramref name="body"/> 为 null）。返回说明，合法时为 null。</summary>
    public static string? ParseBody(string sml, out SecsItem? body)
    {
        body = null;
        if (string.IsNullOrWhiteSpace(sml))
        {
            return null;
        }

        try
        {
            body = SmlParser.ParseItem(sml);
            return null;
        }
        catch (SmlParseException ex)
        {
            return ex.Message;
        }
    }

    /// <summary>设备端状态的中文名称。</summary>
    public static string StateName(HsmsState state) => state switch
    {
        HsmsState.NotConnected => "未连接",
        HsmsState.NotSelected => "未选择",
        HsmsState.Selected => "已选择",
        _ => "未知",
    };

    /// <summary>发送结果的中文说明；成功时为"成功"。被拒绝时附上原因码（十六进制）。</summary>
    public static string Describe(SecsResult result)
    {
        if (result.IsSuccess)
        {
            return "成功";
        }

        string reason = result.RejectReason is { } code ? $"（原因码 0x{code:X2}）" : string.Empty;
        return $"{ErrorName(result.Error)}{reason}：{result.ErrorMessage}";
    }

    private static string ErrorName(SecsError error) => error switch
    {
        SecsError.NotSelected => "会话未选中",
        SecsError.ReplyTimeout => "T3 超时，未收到应答",
        SecsError.Rejected => "对方拒绝（Reject.req）",
        SecsError.Aborted => "对方中止（SxF0）",
        SecsError.InvalidReply => "应答无效",
        SecsError.ConnectionLost => "连接已断开",
        SecsError.Cancelled => "已取消",
        SecsError.InvalidRequest => "请求无效",
        _ => "其它错误",
    };

    private static bool IsPrimaryFunction(int function) => function is >= 1 and <= MaximumFunction && function % 2 == 1;

    private static IEnumerable<(string Name, int Value)> Timers(SecsSettings settings)
    {
        yield return ("T3", settings.T3);
        yield return ("T5", settings.T5);
        yield return ("T6", settings.T6);
        yield return ("T7", settings.T7);
        yield return ("T8", settings.T8);
    }
}

/// <summary>发送区的常用模板：主消息的 S、F、W 位与 SML 正文。</summary>
public sealed record SecsTemplate(string Name, int Stream, int Function, bool ReplyExpected, string Sml);

/// <summary>收到的主消息（有处理器的 S/F 才显示正文，计划 F-4）：时间、S/F 与 W 位、SML 正文。</summary>
public sealed record SecsReceivedMessage(string Time, string Header, string Sml);
