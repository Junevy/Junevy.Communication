namespace Junevy.Communication.WebApi.Server.Tests.TestSupport;

/// <summary>可推进的时间源：去重窗口的过期用它验证，不依赖真实等待。</summary>
internal sealed class ManualTimeProvider : TimeProvider
{
    private DateTimeOffset now = new DateTimeOffset(2026, 10, 17, 0, 0, 0, TimeSpan.Zero);

    /// <inheritdoc />
    public override DateTimeOffset GetUtcNow() => now;

    /// <summary>把时间向前推进。</summary>
    /// <param name="by">推进的时长。</param>
    public void Advance(TimeSpan by) => now += by;
}
