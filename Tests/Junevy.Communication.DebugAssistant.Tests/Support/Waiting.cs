namespace Junevy.Communication.DebugAssistant.Tests.Support;

/// <summary>等待一个条件成立（用于定时器驱动的行为）。超时抛出 <see cref="TimeoutException"/>。</summary>
internal static class Waiting
{
    public static async Task UntilAsync(Func<bool> condition, TimeSpan timeout)
    {
        DateTime deadline = DateTime.UtcNow + timeout;
        while (!condition())
        {
            if (DateTime.UtcNow > deadline)
            {
                throw new TimeoutException("The condition was not met in time.");
            }

            await Task.Delay(10);
        }
    }
}
