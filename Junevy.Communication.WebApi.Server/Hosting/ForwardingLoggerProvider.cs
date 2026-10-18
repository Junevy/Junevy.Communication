using Microsoft.Extensions.Logging;

namespace Junevy.Communication.WebApi.Server.Hosting;

/// <summary>
/// 把嵌入的 ASP.NET Core 应用的日志转给调用方的 <see cref="ILoggerFactory"/>（内部，P32-D3）。
/// 只转发 Warning 及以上（由宿主应用的最低级别控制），与 MQTT 桥接 SDK 日志的做法一致。
/// </summary>
internal sealed class ForwardingLoggerProvider : ILoggerProvider
{
    private readonly ILoggerFactory target;

    /// <summary>创建转发器。</summary>
    /// <param name="target">调用方的日志工厂。</param>
    public ForwardingLoggerProvider(ILoggerFactory target)
    {
        this.target = target;
    }

    /// <inheritdoc />
    public ILogger CreateLogger(string categoryName) => target.CreateLogger(categoryName);

    /// <inheritdoc />
    public void Dispose()
    {
        // 调用方的日志工厂由调用方释放，这里不释放。
    }
}
