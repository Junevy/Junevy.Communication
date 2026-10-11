using Microsoft.Extensions.Logging;

namespace Junevy.Communication.WebApi.Tests;

/// <summary>
/// 把任意 <see cref="ILogger"/> 适配成 <see cref="ILogger{TCategoryName}"/>：客户端构造函数要求带类别的日志记录器，
/// 测试用 <c>TestLogger</c> 记录日志，不需要引用完整的 Microsoft.Extensions.Logging 实现包。
/// </summary>
internal sealed class TypedLogger<T> : ILogger<T>
{
    private readonly ILogger inner;

    public TypedLogger(ILogger inner)
    {
        this.inner = inner;
    }

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => inner.BeginScope(state);

    public bool IsEnabled(LogLevel logLevel) => inner.IsEnabled(logLevel);

    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        => inner.Log(logLevel, eventId, state, exception, formatter);
}
