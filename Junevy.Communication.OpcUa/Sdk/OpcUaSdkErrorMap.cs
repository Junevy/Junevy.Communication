using Junevy.Communication.Core.Results;
using Junevy.Communication.Core.Utils;
using Opc.Ua;

namespace Junevy.Communication.OpcUa.Sdk;

/// <summary>
/// OPC UA 的失败到 <see cref="CommResult"/> 的映射，是全包唯一一处由 SDK 的异常或状态码推出
/// <see cref="CommErrorKind"/> 的地方（P6-C7、Q38）。逐行的映射表见计划第 17.1 节，每一行都有对应的测试。
/// </summary>
internal static class OpcUaSdkErrorMap
{
    // 表里没有的 Bad 状态一律归 RemoteError。
    private static readonly Dictionary<uint, CommErrorKind> ServiceLevelKinds = BuildServiceLevelKinds();

    /// <summary>
    /// 服务级失败的状态码 → 分类。
    /// </summary>
    /// <param name="statusCode">服务器或 SDK 给出的原始状态码。</param>
    /// <returns>对应的 <see cref="CommErrorKind"/>；表里没有的 Bad 状态为 <see cref="CommErrorKind.RemoteError"/>。</returns>
    public static CommErrorKind KindOf(uint statusCode)
        => ServiceLevelKinds.TryGetValue(statusCode, out CommErrorKind kind)
            ? kind
            : CommErrorKind.RemoteError;

    /// <summary>
    /// 单项状态码 → 结果。服务端级失败用它把 Bad 状态变成调用级失败：单节点便捷方法（P62-D8）
    /// 与浏览（P62-D9）。Uncertain 算成功。
    /// </summary>
    /// <param name="status">该项的状态码。</param>
    /// <param name="message">失败说明里用的节点文本。</param>
    /// <returns>Good 或 Uncertain 时成功，否则按 <see cref="KindOf"/> 返回分类与状态码。</returns>
    public static CommResult FromStatus(OpcUaStatusCode status, string message)
        => status.IsBad
            ? CommResult.Fail($"{message}: {status}.", KindOf(status.Code), status.Code)
            : CommResult.Success();

    /// <summary>单项状态码 → 带数据的结果，供读、写与订阅的单节点便捷方法使用。</summary>
    /// <param name="status">该项的状态码。</param>
    /// <param name="message">失败说明里用的节点文本。</param>
    /// <param name="data">状态不算 Bad 时返回的数据。</param>
    /// <typeparam name="T">数据类型。</typeparam>
    /// <returns>Good 或 Uncertain 时成功并带上数据，否则按 <see cref="KindOf"/> 返回分类与状态码。</returns>
    public static CommResult<T> FromStatus<T>(OpcUaStatusCode status, string message, T data)
        => status.IsBad
            ? CommResult<T>.Fail($"{message}: {status}.", KindOf(status.Code), status.Code)
            : CommResult<T>.Success(data);

    /// <summary>
    /// 运行期异常 → 结果。SDK 抛出的异常不能逃出公开方法（P6-C8）。
    /// </summary>
    /// <param name="exception">SDK 或 BCL 抛出的异常。</param>
    /// <param name="scope">包住这次操作的超时作用域；给了它就以"是不是我们自己超时 / 调用方取消"为准，
    /// 不再猜异常的类型——SDK 的 <c>ConnectAsync</c> 没有超时参数，被中止时抛的是
    /// <c>TaskCanceledException</c>，类型里读不出是我们超时还是调用方取消（计划第 19.2.13 节的 F2）。</param>
    /// <returns>带分类的结果；异常只作诊断。</returns>
    public static CommResult Runtime(Exception exception, TimeoutScope? scope = null)
    {
        ArgumentNullException.ThrowIfNull(exception);

        // 先看作用域：调用方取消与超时由我们自己掌握，比异常的类型可靠。
        if (FromScope(scope, exception) is CommResult scoped)
        {
            return scoped;
        }

        return FromException(exception);
    }

    /// <summary>作用域说是取消或超时就以它为准；它什么也没说时返回 null，交给异常本身分类。</summary>
    /// <param name="scope">包住这次操作的超时作用域；可以是 null。</param>
    /// <param name="exception">只作诊断附在结果上。</param>
    /// <returns>分类结果；作用域没有给出结论时为 null。</returns>
    private static CommResult? FromScope(TimeoutScope? scope, Exception exception)
    {
        if (scope is null)
        {
            return null;
        }

        if (scope.IsUserCancelled)
        {
            return Fail(CommErrorKind.Cancelled, null, "The operation was cancelled.", exception);
        }

        return scope.IsTimedOut
            ? Fail(CommErrorKind.Timeout, null, "The OPC UA operation timed out.", exception)
            : null;
    }

    private static CommResult FromException(Exception exception)
    {
        // SDK 把服务级失败包在 ServiceResultException 里，状态码要沿异常链取（F16）。
        for (Exception? current = exception; current is not null; current = current.InnerException)
        {
            if (current is ServiceResultException service && service.StatusCode.Code != 0)
            {
                return FromStatus(new OpcUaStatusCode(service.StatusCode.Code), $"The server reported {service.StatusCode}")
                    .WithException(exception);
            }
        }

        return exception switch
        {
            OperationCanceledException => Fail(CommErrorKind.Cancelled, null, "The operation was cancelled.", exception),
            TimeoutException => Fail(CommErrorKind.Timeout, null, "The OPC UA operation timed out.", exception),
            System.Net.Sockets.SocketException => Fail(CommErrorKind.ConnectionClosed, null, $"The connection failed: {exception.Message}", exception),
            IOException => Fail(CommErrorKind.ConnectionClosed, null, $"The connection failed: {exception.Message}", exception),

            // 调用进行中会话被并发的断开或释放关掉：SDK 抛"对象已释放"，按"释放之后的操作"归类（第 17.1 节）。
            ObjectDisposedException => Fail(CommErrorKind.ConnectionClosed, null, "The OPC UA session was closed during the call.", exception),
            _ => Fail(CommErrorKind.Unspecified, null, $"The OPC UA operation failed with {exception.GetType().Name}: {exception.Message}", exception),
        };
    }

    /// <summary>会话不是 Connected（P62-D11：不调用 SDK，直接返回）。</summary>
    /// <param name="message">失败说明。</param>
    /// <returns><see cref="CommErrorKind.NotConnected"/> 的结果。</returns>
    public static CommResult NotConnected(string message) => Fail(CommErrorKind.NotConnected, null, message, null);

    /// <summary>服务器没有 None 端点，或没有匿名令牌策略（P62-D3）。</summary>
    /// <param name="message">失败说明。</param>
    /// <returns><see cref="CommErrorKind.NotSupported"/> 的结果。</returns>
    public static CommResult NotSupported(string message) => Fail(CommErrorKind.NotSupported, null, message, null);

    /// <summary>请求本身不合法：NodeId 文本非法、命名空间 URI 未知、写入类型不受支持（P62-C8：不发送）。</summary>
    /// <param name="message">写明具体哪一项非法。</param>
    /// <param name="exception">SDK 解析器给出的异常；只作诊断。</param>
    /// <returns><see cref="CommErrorKind.InvalidRequest"/> 的结果。</returns>
    public static CommResult InvalidRequest(string message, Exception? exception = null)
        => CommResult.Fail(message, CommErrorKind.InvalidRequest, exception: exception);

    /// <summary>释放之后的调用（P6-C8）。</summary>
    /// <param name="message">失败说明。</param>
    /// <returns><see cref="CommErrorKind.ConnectionClosed"/> 的结果。</returns>
    public static CommResult ConnectionClosed(string message) => Fail(CommErrorKind.ConnectionClosed, null, message, null);

    private static Dictionary<uint, CommErrorKind> BuildServiceLevelKinds()
    {
        var kinds = new Dictionary<uint, CommErrorKind>();

        Add(kinds, CommErrorKind.Timeout,
            StatusCodes.BadTimeout, StatusCodes.BadRequestTimeout);

        Add(kinds, CommErrorKind.ConnectionClosed,
            StatusCodes.BadNotConnected,
            StatusCodes.BadConnectionClosed,
            StatusCodes.BadSecureChannelClosed,
            StatusCodes.BadSessionClosed,
            StatusCodes.BadSessionIdInvalid,
            StatusCodes.BadServerNotConnected,
            StatusCodes.BadCommunicationError);

        Add(kinds, CommErrorKind.AuthenticationFailed,
            StatusCodes.BadIdentityTokenInvalid,
            StatusCodes.BadIdentityTokenRejected,
            StatusCodes.BadUserAccessDenied,
            StatusCodes.BadSecurityChecksFailed,
            StatusCodes.BadCertificateInvalid,
            StatusCodes.BadCertificateHostNameInvalid,
            StatusCodes.BadCertificateUriInvalid,
            StatusCodes.BadCertificateUseNotAllowed,
            StatusCodes.BadCertificateIssuerTimeInvalid,
            StatusCodes.BadCertificateIssuerUseNotAllowed,
            StatusCodes.BadCertificatePolicyCheckFailed,
            StatusCodes.BadCertificateRevocationUnknown,
            StatusCodes.BadCertificateRevoked,
            StatusCodes.BadCertificateChainIncomplete,
            StatusCodes.BadCertificateTimeInvalid,
            StatusCodes.BadCertificateIssuerRevocationUnknown,
            StatusCodes.BadCertificateIssuerRevoked,
            StatusCodes.BadCertificateUntrusted);

        Add(kinds, CommErrorKind.ResourceExhausted,
            StatusCodes.BadTooManySessions,
            StatusCodes.BadTooManyOperations,
            StatusCodes.BadTooManySubscriptions,
            StatusCodes.BadTooManyMonitoredItems,
            StatusCodes.BadOutOfMemory,
            StatusCodes.BadResourceUnavailable);

        Add(kinds, CommErrorKind.NotSupported,
            StatusCodes.BadServiceUnsupported,
            StatusCodes.BadNotSupported,
            StatusCodes.BadSecurityPolicyRejected,
            StatusCodes.BadSecurityModeRejected);

        Add(kinds, CommErrorKind.ProtocolViolation,
            StatusCodes.BadEncodingError,
            StatusCodes.BadDecodingError,
            StatusCodes.BadEncodingLimitsExceeded);

        return kinds;
    }

    private static void Add(Dictionary<uint, CommErrorKind> kinds, CommErrorKind kind, params StatusCode[] codes)
    {
        foreach (StatusCode code in codes)
        {
            kinds[code.Code] = kind;
        }
    }

    private static CommResult Fail(CommErrorKind kind, long? protocolErrorCode, string message, Exception? exception)
        => CommResult.Fail(message, kind, protocolErrorCode, exception);

    private static CommResult WithException(this CommResult result, Exception exception)
        => result.IsSuccess
            ? result
            : CommResult.Fail(result.ErrorMessage, result.ErrorKind, result.ProtocolErrorCode, exception);
}
