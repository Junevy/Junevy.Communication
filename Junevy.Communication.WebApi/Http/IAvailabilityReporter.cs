using Junevy.Communication.Core.Results;

namespace Junevy.Communication.WebApi.Http;

/// <summary>
/// 接收每次已发出的尝试的传输层结果（内部）：尝试循环报告，可用性跟踪据此维护 <see cref="WebApiAvailability"/>。
/// </summary>
internal interface IAvailabilityReporter
{
    /// <summary>
    /// 报告一次已发出的尝试。
    /// </summary>
    /// <param name="transportKind">传输层结果：收到响应为 <see cref="CommErrorKind.None"/>；否则是网络层失败的分类（连接中断、超时、取消、请求非法、正文超限）。</param>
    /// <param name="statusCode">已收到的 HTTP 状态码；没有收到响应头时为 null。</param>
    /// <param name="exception">底层异常，仅供诊断；没有时为 null。</param>
    void ReportAttempt(CommErrorKind transportKind, int? statusCode, Exception? exception);
}
