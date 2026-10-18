using Junevy.Communication.Ftp;

namespace Junevy.Communication.Ftp.Tests.TestSupport;

/// <summary>同步记录进度报告的收集器（线程安全；SDK 的回调线程上调用）。</summary>
public sealed class TransferProgressRecorder : IProgress<FtpTransferProgress>
{
    private readonly object sync = new();
    private readonly List<FtpTransferProgress> reports = new();

    /// <summary>收到一次进度报告。</summary>
    /// <param name="value">进度。</param>
    public void Report(FtpTransferProgress value)
    {
        lock (sync)
        {
            reports.Add(value);
        }
    }

    /// <summary>按收到的顺序返回全部报告的副本。</summary>
    /// <returns>报告列表。</returns>
    public IReadOnlyList<FtpTransferProgress> Snapshot()
    {
        lock (sync)
        {
            return reports.ToList();
        }
    }
}
