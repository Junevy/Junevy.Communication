using System.Text.Json.Serialization;
using Junevy.Communication.DebugAssistant.Workspace;
using Junevy.Communication.Secs.Hsms;

namespace Junevy.Communication.DebugAssistant.Protocols.Secs;

/// <summary>
/// SECS/HSMS 设备端页签的参数（计划第 7.6 节、DA-D11）：连接模式、地址与设备号、计时器、Linktest 与报文长度上限，以及自动应答规则。
/// 会话状态与收发的消息不保存。
/// </summary>
public sealed record SecsSettings(
    HsmsConnectionMode Mode,
    string? Host,
    int Port,
    int DeviceId,
    int T3,
    int T5,
    int T6,
    int T7,
    int T8,
    int LinktestInterval,
    int MaxMessageLength,
    SecsRule[] Rules)
    : SessionSettings;

/// <summary>
/// 一条自动应答规则（计划第 7.6 节）：某个 S/F 的主消息按 <see cref="Kind"/> 处理。
/// <see cref="SecsRuleKind.Reply"/> 用 <see cref="Sml"/> 应答（没有 W 位时不应答）；<see cref="SecsRuleKind.Abort"/> 不应答，带 W 位的主消息收到 SxF0。
/// </summary>
public sealed record SecsRule(byte Stream, byte Function, SecsRuleKind Kind, string Sml)
{
    /// <summary>界面上显示的应答方式。</summary>
    [JsonIgnore]
    public string KindText => Kind == SecsRuleKind.Reply ? "SML 应答" : "中止（SxF0）";
}

/// <summary>自动应答规则的应答方式。</summary>
public enum SecsRuleKind
{
    /// <summary>用规则中的 SML 应答。</summary>
    Reply,

    /// <summary>中止：返回 null，带 W 位的主消息收到 SxF0。</summary>
    Abort,
}
