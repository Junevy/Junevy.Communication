using Junevy.Communication.DebugAssistant.Sessions;
using Junevy.Communication.DebugAssistant.Shell;

namespace Junevy.Communication.DebugAssistant.Tests.Support;

/// <summary>记录释放次数与释放时是否仍在页签集合中的测试页签。</summary>
internal sealed class TrackingSession : SessionViewModel
{
    public TrackingSession(ProtocolDescriptor descriptor)
        : base(descriptor)
    {
    }

    public int DisposeCount { get; private set; }

    /// <summary>释放开始时页签是否仍在集合里（关闭流程要求：先释放，再移除）。</summary>
    public bool? ListedWhenDisposed { get; private set; }

    /// <summary>由测试设置，返回页签当前是否仍在集合里。</summary>
    public Func<bool>? IsListed { get; set; }

    /// <summary>设置后，释放会等待它完成（用于测试并发与超时）。</summary>
    public TaskCompletionSource? Gate { get; set; }

    /// <summary>设置后，释放会抛出该异常。</summary>
    public Exception? DisposeFailure { get; set; }

    public override async ValueTask DisposeAsync()
    {
        DisposeCount++;
        ListedWhenDisposed = IsListed?.Invoke();
        if (Gate is not null)
        {
            await Gate.Task;
        }

        if (DisposeFailure is not null)
        {
            throw DisposeFailure;
        }
    }
}
