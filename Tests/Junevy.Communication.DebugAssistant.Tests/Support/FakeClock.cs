namespace Junevy.Communication.DebugAssistant.Tests.Support;

/// <summary>可手动推进的时钟，用于验证时间戳与统计的最后收发时间。</summary>
internal sealed class FakeClock : TimeProvider
{
    private DateTimeOffset _now = new(2026, 10, 20, 9, 0, 0, TimeSpan.Zero);

    public override DateTimeOffset GetUtcNow() => _now;

    public void Advance(TimeSpan by) => _now += by;
}
