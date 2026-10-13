namespace Junevy.Communication.Channels;

/// <summary>
/// 单次请求或接收的选项。
/// </summary>
public sealed class RequestOptions
{
    /// <summary>
    /// 等待超时（毫秒）：<c>RequestAsync</c> 是等应答的时长，<c>ReceiveAsync</c> 是等下一帧的时长（等待者从注册时开始计时）。
    /// 为 null 时使用配置中的 <c>RequestTimeout</c>（TCP 默认 2000 毫秒）——等待时长比它长的用法必须显式给值。
    /// </summary>
    public int? Timeout { get; set; }

    /// <summary>应答判定器；为 null 时按 <see cref="CorrelationMode"/> 的默认规则判定。Keyed 模式下 RequestAsync 忽略此项（按关联键匹配）；ReceiveAsync 在所有模式下都使用此项。</summary>
    public IResponseMatcher? Matcher { get; set; }
}
