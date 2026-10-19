using Junevy.Communication.Core.Results;
using Junevy.Communication.Secs;
using Junevy.Communication.WebApi;

namespace Junevy.Communication.DebugAssistant.Sessions;

/// <summary>
/// 把三种结果（<see cref="CommResult"/>、<see cref="WebApiResult{T}"/>、<see cref="SecsResult"/>）统一映射为
/// <see cref="OperationRecord"/>（计划 U6：各页面不再各写一套）。
/// </summary>
public static class OperationRecords
{
    public static OperationRecord From(DateTimeOffset time, string operation, string target, TimeSpan elapsed, CommResult result) =>
        Create(time, operation, target, elapsed, result.IsSuccess, result.ErrorKind, HexCode(result.ProtocolErrorCode), result.ErrorMessage);

    public static OperationRecord From<T>(DateTimeOffset time, string operation, string target, TimeSpan elapsed, CommResult<T> result) =>
        Create(time, operation, target, elapsed, result.IsSuccess, result.ErrorKind, HexCode(result.ProtocolErrorCode), result.ErrorMessage);

    /// <summary>WebApi 的协议码是远端业务码（有则显示），否则显示 HTTP 状态码（十进制，例如 "HTTP 404"）。</summary>
    public static OperationRecord From<T>(DateTimeOffset time, string operation, string target, TimeSpan elapsed, WebApiResult<T> result)
    {
        string? code = result.RemoteCode ?? (result.StatusCode is { } status ? $"HTTP {status}" : null);
        return Create(time, operation, target, elapsed, result.IsSuccess, result.ErrorKind, code, result.ErrorMessage);
    }

    /// <summary>SECS 的协议码是拒绝原因码（有则显示，十六进制）；失败消息没有时使用错误的名称。</summary>
    public static OperationRecord From(DateTimeOffset time, string operation, string target, TimeSpan elapsed, SecsResult result) =>
        Create(
            time,
            operation,
            target,
            elapsed,
            result.IsSuccess,
            result.ErrorKind,
            result.RejectReason is { } reason ? $"0x{reason:X2}" : null,
            result.ErrorMessage ?? (result.IsSuccess ? string.Empty : result.Error.ToString()));

    private static OperationRecord Create(
        DateTimeOffset time,
        string operation,
        string target,
        TimeSpan elapsed,
        bool succeeded,
        CommErrorKind kind,
        string? code,
        string? message) =>
        new(time, operation, target, elapsed, succeeded, succeeded ? CommErrorKind.None : kind, code, message ?? string.Empty);

    private static string? HexCode(long? code) => code is { } value ? $"0x{value:X2}" : null;
}
