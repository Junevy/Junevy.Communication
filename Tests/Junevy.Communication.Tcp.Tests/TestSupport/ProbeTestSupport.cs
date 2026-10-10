using Junevy.Communication.Channels;
using Junevy.Communication.Core.Results;

namespace Junevy.Communication.Tcp.Tests;

/// <summary>以委托实现的心跳探测（测试用）。</summary>
internal sealed class DelegateProbe : IHealthProbe
{
    private readonly Func<CancellationToken, Task<CommResult>> probe;

    public DelegateProbe(Func<CancellationToken, Task<CommResult>> probe)
    {
        this.probe = probe;
    }

    public Task<CommResult> ProbeAsync(CancellationToken cancellationToken) => probe(cancellationToken);
}
