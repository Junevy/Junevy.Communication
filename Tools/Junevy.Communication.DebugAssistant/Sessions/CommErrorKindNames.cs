using Junevy.Communication.Core.Results;

namespace Junevy.Communication.DebugAssistant.Sessions;

/// <summary>错误种类的中文名称（DA-D17）。库的错误消息不翻译，显示在旁边。</summary>
public static class CommErrorKindNames
{
    /// <summary>错误种类的中文名称。</summary>
    public static string Of(CommErrorKind kind) => kind switch
    {
        CommErrorKind.None => "成功",
        CommErrorKind.Unspecified => "未分类失败",
        CommErrorKind.InvalidRequest => "请求无效",
        CommErrorKind.ConnectionClosed => "连接已关闭",
        CommErrorKind.Timeout => "超时",
        CommErrorKind.ProtocolViolation => "协议违规",
        CommErrorKind.RemoteError => "对端返回错误",
        CommErrorKind.Cancelled => "已取消",
        CommErrorKind.NotConnected => "未连接",
        CommErrorKind.AuthenticationFailed => "认证失败",
        CommErrorKind.ResourceExhausted => "资源耗尽",
        CommErrorKind.NotSupported => "不支持",
        _ => kind.ToString(),
    };
}
