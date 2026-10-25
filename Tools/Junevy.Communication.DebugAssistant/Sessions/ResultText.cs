using Junevy.Communication.Core.Results;

namespace Junevy.Communication.DebugAssistant.Sessions;

/// <summary>失败结果的界面文字（页签的消息区用）：错误种类的中文名称，加上库给出的消息。</summary>
public static class ResultText
{
    /// <summary>失败的说明，形如 "请求无效：…"；库没有给消息时只有种类名称。</summary>
    public static string Failure(CommErrorKind kind, string? message) =>
        string.IsNullOrEmpty(message) ? CommErrorKindNames.Of(kind) : $"{CommErrorKindNames.Of(kind)}：{message}";
}
