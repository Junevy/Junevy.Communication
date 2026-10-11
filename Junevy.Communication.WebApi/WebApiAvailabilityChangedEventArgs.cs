using Junevy.Communication.Core.Results;

namespace Junevy.Communication.WebApi;

/// <summary>
/// <see cref="IWebApiEndpoint.AvailabilityChanged"/> 的参数。自定义实现也可以用公开构造函数创建后引发事件。
/// </summary>
public sealed class WebApiAvailabilityChangedEventArgs : EventArgs
{
    /// <summary>创建参数，保存全部参数。</summary>
    /// <param name="endpointName">端点名称。</param>
    /// <param name="previousAvailability">变化前的可用性。</param>
    /// <param name="currentAvailability">变化后的可用性。</param>
    /// <param name="reason">导致变为 <see cref="WebApiAvailability.Offline"/> 的最后一次失败的分类；其余变化为 <see cref="CommErrorKind.None"/>。</param>
    /// <param name="exception">相关异常，仅供诊断；没有时为 null。</param>
    /// <exception cref="ArgumentNullException"><paramref name="endpointName"/> 为 null。</exception>
    public WebApiAvailabilityChangedEventArgs(string endpointName, WebApiAvailability previousAvailability,
                                              WebApiAvailability currentAvailability, CommErrorKind reason, Exception? exception)
    {
        EndpointName = endpointName ?? throw new ArgumentNullException(nameof(endpointName));
        PreviousAvailability = previousAvailability;
        CurrentAvailability = currentAvailability;
        Reason = reason;
        Exception = exception;
    }

    /// <summary>端点名称。</summary>
    public string EndpointName { get; }

    /// <summary>变化前的可用性。</summary>
    public WebApiAvailability PreviousAvailability { get; }

    /// <summary>变化后的可用性。</summary>
    public WebApiAvailability CurrentAvailability { get; }

    /// <summary>导致变为 <see cref="WebApiAvailability.Offline"/> 的最后一次失败的分类；其余变化为 <see cref="CommErrorKind.None"/>。</summary>
    public CommErrorKind Reason { get; }

    /// <summary>相关异常，仅供诊断；没有时为 null。</summary>
    public Exception? Exception { get; }
}
