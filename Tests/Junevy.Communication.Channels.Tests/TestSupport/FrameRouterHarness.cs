using System.Text;
using Junevy.Communication.Channels.Pipeline;
using Junevy.Communication.Testing;

namespace Junevy.Communication.Channels.Tests;

/// <summary>
/// 测试辅助：记录派发给 <c>raise</c> 的帧（线程安全）。
/// </summary>
internal sealed class RecordingSink
{
    private readonly object sync = new object();
    private readonly List<FrameReceivedEventArgs> received = new List<FrameReceivedEventArgs>();

    /// <summary>作为路由的 <c>raise</c> 回调使用。</summary>
    public Func<FrameReceivedEventArgs, Task> Raise => Handle;

    /// <summary>已派发的帧数。</summary>
    public int Count
    {
        get
        {
            lock (sync)
                return received.Count;
        }
    }

    /// <summary>已派发的帧内容（按派发顺序）。</summary>
    public IReadOnlyList<byte[]> Frames
    {
        get
        {
            lock (sync)
                return received.Select(args => args.Data).ToList();
        }
    }

    /// <summary>派发一帧（可由测试的自定义 <c>raise</c> 调用）。</summary>
    public Task Handle(FrameReceivedEventArgs args)
    {
        lock (sync)
            received.Add(args);

        return Task.CompletedTask;
    }
}

/// <summary>
/// 测试辅助：路由测试的装配（日志、统计、关联表、派发记录与路由）。派发循环在 <see cref="Start"/> 后运行。
/// </summary>
internal sealed class RouterRig : IAsyncDisposable
{
    /// <summary>
    /// 创建装配。
    /// </summary>
    /// <param name="mode">关联模式。</param>
    /// <param name="lateReplyWindow">迟到应答窗口（毫秒）；-1 表示等于各请求的超时。</param>
    /// <param name="keys">关联键提取器；Keyed 模式必须提供。</param>
    /// <param name="capacity">派发队列容量。</param>
    /// <param name="fullMode">队列满时的处理方式。</param>
    /// <param name="raise">派发回调；为 null 时使用 <see cref="Sink"/>。</param>
    /// <param name="logger">日志器；为 null 时新建。</param>
    public RouterRig(CorrelationMode mode, int lateReplyWindow = -1, IFrameKeyExtractor? keys = null, int capacity = 1024,
                     QueueFullMode fullMode = QueueFullMode.Wait, Func<FrameReceivedEventArgs, Task>? raise = null,
                     TestLogger? logger = null)
    {
        Logger = logger ?? new TestLogger();
        Statistics = new ConnectionStatistics();
        Sink = new RecordingSink();
        Table = new PendingRequestTable(mode, keys, lateReplyWindow, Logger);
        Router = new FrameRouter(Table, capacity, fullMode, raise ?? Sink.Raise, Statistics, Logger);
    }

    /// <summary>日志（同时用于断言 Warning / Error）。</summary>
    public TestLogger Logger { get; }

    /// <summary>连接统计（断言 FramesDropped）。</summary>
    public ConnectionStatistics Statistics { get; }

    /// <summary>默认派发记录。</summary>
    public RecordingSink Sink { get; }

    /// <summary>关联表。</summary>
    public PendingRequestTable Table { get; }

    /// <summary>帧路由。</summary>
    public FrameRouter Router { get; }

    /// <summary>启动派发循环。</summary>
    public void Start() => Router.Start();

    /// <summary>停止派发循环。</summary>
    public ValueTask DisposeAsync() => Router.StopAsync(0);
}

/// <summary>
/// 测试用关联键提取器：请求与应答的前 4 字节为大端关联键。
/// </summary>
internal sealed class PrefixKeyExtractor : IFrameKeyExtractor
{
    /// <inheritdoc />
    public bool TryGetRequestKey(ReadOnlySpan<byte> request, out long key) => TryReadKey(request, out key);

    /// <inheritdoc />
    public bool TryGetResponseKey(ReadOnlySpan<byte> frame, out long key) => TryReadKey(frame, out key);

    /// <summary>构造"4 字节大端键 + 文本"的帧。</summary>
    public static byte[] Frame(int key, string text)
    {
        byte[] body = Encoding.ASCII.GetBytes(text);
        var frame = new byte[4 + body.Length];
        frame[0] = (byte)(key >> 24);
        frame[1] = (byte)(key >> 16);
        frame[2] = (byte)(key >> 8);
        frame[3] = (byte)key;
        Array.Copy(body, 0, frame, 4, body.Length);
        return frame;
    }

    private static bool TryReadKey(ReadOnlySpan<byte> data, out long key)
    {
        if (data.Length < 4)
        {
            key = 0;
            return false;
        }

        key = ((long)data[0] << 24) | ((long)data[1] << 16) | ((long)data[2] << 8) | data[3];
        return true;
    }
}

/// <summary>
/// 测试用应答判定器：应答的首字节等于请求的首字节即匹配；空请求（接收等待者）匹配任意帧。
/// </summary>
internal sealed class LeadingByteMatcher : IResponseMatcher
{
    /// <inheritdoc />
    public bool IsMatch(ReadOnlySpan<byte> request, ReadOnlySpan<byte> frame)
        => request.Length == 0 || (frame.Length > 0 && frame[0] == request[0]);
}

/// <summary>
/// 测试用接收判定器：只认领首字节等于 <see cref="Tag"/> 的帧，与请求无关（接收等待者没有请求）。
/// </summary>
internal sealed class TagMatcher : IResponseMatcher
{
    /// <summary>创建判定器。</summary>
    /// <param name="tag">要认领的首字节。</param>
    public TagMatcher(byte tag)
    {
        Tag = tag;
    }

    /// <summary>要认领的首字节。</summary>
    public byte Tag { get; }

    /// <inheritdoc />
    public bool IsMatch(ReadOnlySpan<byte> request, ReadOnlySpan<byte> frame)
        => frame.Length > 0 && frame[0] == Tag;
}

/// <summary>
/// 路由测试的通用辅助方法。
/// </summary>
internal static class RouterTestHelpers
{
    /// <summary>ASCII 字节。</summary>
    public static byte[] Ascii(string text) => Encoding.ASCII.GetBytes(text);

    /// <summary>以首字节标识的帧（用于 <see cref="LeadingByteMatcher"/>）。</summary>
    public static byte[] Tagged(byte tag, string text)
    {
        byte[] body = Encoding.ASCII.GetBytes(text);
        var frame = new byte[1 + body.Length];
        frame[0] = tag;
        Array.Copy(body, 0, frame, 1, body.Length);
        return frame;
    }

    /// <summary>等待任务在给定时间内完成，并返回其结果。</summary>
    public static async Task<T> WithinAsync<T>(Task<T> task, int milliseconds)
    {
        Task completed = await Task.WhenAny(task, Task.Delay(milliseconds)).ConfigureAwait(false);
        Assert.True(ReferenceEquals(completed, task), $"The operation did not complete within {milliseconds} ms.");
        return await task.ConfigureAwait(false);
    }

    /// <summary>等待任务在给定时间内完成。</summary>
    public static async Task WithinAsync(Task task, int milliseconds)
    {
        Task completed = await Task.WhenAny(task, Task.Delay(milliseconds)).ConfigureAwait(false);
        Assert.True(ReferenceEquals(completed, task), $"The operation did not complete within {milliseconds} ms.");
        await task.ConfigureAwait(false);
    }

    /// <summary>轮询直到条件成立或超时（超时则断言失败）。</summary>
    public static async Task WaitUntilAsync(Func<bool> condition, int milliseconds)
    {
        DateTime deadline = DateTime.UtcNow.AddMilliseconds(milliseconds);
        while (!condition() && DateTime.UtcNow < deadline)
            await Task.Delay(5).ConfigureAwait(false);

        Assert.True(condition(), "The condition was not met within the time limit.");
    }

    /// <summary>等待派发记录达到指定数量。</summary>
    public static Task WaitForCountAsync(RecordingSink sink, int count)
        => WaitUntilAsync(() => sink.Count >= count, 3000);
}
