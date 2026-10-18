namespace Junevy.Communication.WebApi.Server.Tests.TestSupport;

/// <summary>
/// 记录未观察的任务异常（Q40）：用例结束时强制 GC，断言没有任务异常逃逸到 <see cref="TaskScheduler.UnobservedTaskException"/>。
/// </summary>
internal sealed class UnobservedTaskWatch : IDisposable
{
    private readonly List<Exception> exceptions = new List<Exception>();
    private readonly object gate = new object();

    /// <summary>开始监视。</summary>
    public UnobservedTaskWatch()
    {
        TaskScheduler.UnobservedTaskException += OnUnobserved;
    }

    /// <summary>强制回收并断言没有未观察的异常。</summary>
    public async Task AssertNoneAsync()
    {
        for (int i = 0; i < 3; i++)
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
            await Task.Delay(20).ConfigureAwait(false);
        }

        lock (gate)
            Assert.Empty(exceptions);
    }

    /// <inheritdoc />
    public void Dispose() => TaskScheduler.UnobservedTaskException -= OnUnobserved;

    private void OnUnobserved(object? sender, UnobservedTaskExceptionEventArgs e)
    {
        lock (gate)
            exceptions.Add(e.Exception);
    }
}
