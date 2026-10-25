namespace Junevy.Communication.DebugAssistant.Tests.Support;

/// <summary>等待一个条件成立（用于定时器与库的事件驱动的行为）。超时抛出 <see cref="TimeoutException"/>。</summary>
internal static class Waiting
{
    /// <param name="condition">要等待成立的条件。</param>
    /// <param name="timeout">最长等待时间。</param>
    /// <param name="pump">每次检查前执行，例如排空 <see cref="ManualUiDispatcher"/> 的待执行动作。</param>
    public static async Task UntilAsync(Func<bool> condition, TimeSpan timeout, Action? pump = null)
    {
        DateTime deadline = DateTime.UtcNow + timeout;
        while (true)
        {
            pump?.Invoke();
            if (condition())
            {
                return;
            }

            if (DateTime.UtcNow > deadline)
            {
                throw new TimeoutException("The condition was not met in time.");
            }

            await Task.Delay(10);
        }
    }
}
