namespace Junevy.Communication.Ftp;

/// <summary>
/// 交给 SDK 的流视图（内部）。SDK 会把可定位的流定位到 0，并可能释放传入的流，因此库总是传入这个视图（P63-D6 的流约定，第 18.2 节 F23）：
/// <list type="bullet">
/// <item>视图的起点是调用方流的当前位置：内层流可定位时，视图的位置与长度都相对于这个起点，SDK 定位到 0 就是回到起点，
/// 并且能从长度算出进度的百分比；内层流不可定位时，视图只能顺序读写。</item>
/// <item>同步、异步与 <see cref="Span{T}"/> 的读写都转发给内层流，只支持异步的流（例如 ASP.NET Core 的请求体）同样可用，
/// 取消令牌也传给内层流。</item>
/// <item>释放视图不关闭调用方的流。</item>
/// </list>
/// </summary>
internal sealed class FtpStreamView : Stream
{
    private readonly Stream inner;
    private readonly long start;
    private long transferredBytes;

    /// <summary>包住调用方的流，以它的当前位置为起点。</summary>
    /// <param name="inner">调用方的流；视图不拥有它。</param>
    public FtpStreamView(Stream inner)
    {
        this.inner = inner;
        start = inner.CanSeek ? inner.Position : 0;
    }

    /// <summary>经视图读出或写入的字节数（用于操作日志）。</summary>
    public long TransferredBytes => Interlocked.Read(ref transferredBytes);

    /// <inheritdoc />
    public override bool CanRead => inner.CanRead;

    /// <inheritdoc />
    public override bool CanSeek => inner.CanSeek;

    /// <inheritdoc />
    public override bool CanWrite => inner.CanWrite;

    /// <inheritdoc />
    public override long Length => Math.Max(0, RequireSeekable().Length - start);

    /// <inheritdoc />
    public override long Position
    {
        get => RequireSeekable().Position - start;
        set => MoveTo(value);
    }

    /// <inheritdoc />
    public override void Flush() => inner.Flush();

    /// <inheritdoc />
    public override Task FlushAsync(CancellationToken cancellationToken) => inner.FlushAsync(cancellationToken);

    /// <inheritdoc />
    public override int Read(byte[] buffer, int offset, int count) => Counted(inner.Read(buffer, offset, count));

    /// <inheritdoc />
    public override int Read(Span<byte> buffer) => Counted(inner.Read(buffer));

    /// <inheritdoc />
    public override async Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
        Counted(await inner.ReadAsync(buffer.AsMemory(offset, count), cancellationToken).ConfigureAwait(false));

    /// <inheritdoc />
    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
        Counted(await inner.ReadAsync(buffer, cancellationToken).ConfigureAwait(false));

    /// <inheritdoc />
    public override void Write(byte[] buffer, int offset, int count)
    {
        inner.Write(buffer, offset, count);
        Counted(count);
    }

    /// <inheritdoc />
    public override void Write(ReadOnlySpan<byte> buffer)
    {
        inner.Write(buffer);
        Counted(buffer.Length);
    }

    /// <inheritdoc />
    public override async Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
    {
        await inner.WriteAsync(buffer.AsMemory(offset, count), cancellationToken).ConfigureAwait(false);
        Counted(count);
    }

    /// <inheritdoc />
    public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
    {
        await inner.WriteAsync(buffer, cancellationToken).ConfigureAwait(false);
        Counted(buffer.Length);
    }

    /// <inheritdoc />
    public override long Seek(long offset, SeekOrigin origin)
    {
        long target = origin switch
        {
            SeekOrigin.Begin => offset,
            SeekOrigin.Current => Position + offset,
            SeekOrigin.End => Length + offset,
            _ => throw new ArgumentOutOfRangeException(nameof(origin)),
        };
        MoveTo(target);
        return target;
    }

    /// <inheritdoc />
    public override void SetLength(long value)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(value);
        RequireSeekable().SetLength(start + value);
    }

    // 不关闭调用方的流：视图只是一层转发。
    protected override void Dispose(bool disposing)
    {
    }

    private void MoveTo(long position)
    {
        if (position < 0)
        {
            throw new IOException("The position cannot be moved before the start of the view.");
        }

        RequireSeekable().Position = start + position;
    }

    private Stream RequireSeekable() =>
        inner.CanSeek ? inner : throw new NotSupportedException("The caller's stream cannot seek, so the view is forward-only.");

    private int Counted(int count)
    {
        Interlocked.Add(ref transferredBytes, count);
        return count;
    }
}
