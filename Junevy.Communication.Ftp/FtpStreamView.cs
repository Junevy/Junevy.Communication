namespace Junevy.Communication.Ftp;

/// <summary>
/// 交给 SDK 的流视图（内部）。SDK 会忽略流的当前位置，并可能释放传入的流，因此库总是传入这个视图：
/// 它不可定位（读写从调用方流的当前位置开始），释放视图不会关闭调用方的流（P63-D6 的流约定）。
/// </summary>
internal sealed class FtpStreamView : Stream
{
    private readonly Stream inner;

    /// <summary>包住调用方的流。</summary>
    /// <param name="inner">调用方的流；视图不拥有它。</param>
    public FtpStreamView(Stream inner)
    {
        this.inner = inner;
    }

    /// <inheritdoc />
    public override bool CanRead => inner.CanRead;

    /// <inheritdoc />
    public override bool CanSeek => false;

    /// <inheritdoc />
    public override bool CanWrite => inner.CanWrite;

    /// <inheritdoc />
    public override long Length => throw new NotSupportedException("The view is forward-only.");

    /// <inheritdoc />
    public override long Position
    {
        get => throw new NotSupportedException("The view is forward-only.");
        set => throw new NotSupportedException("The view is forward-only.");
    }

    /// <inheritdoc />
    public override void Flush() => inner.Flush();

    /// <inheritdoc />
    public override int Read(byte[] buffer, int offset, int count) => inner.Read(buffer, offset, count);

    /// <inheritdoc />
    public override void Write(byte[] buffer, int offset, int count) => inner.Write(buffer, offset, count);

    /// <inheritdoc />
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException("The view is forward-only.");

    /// <inheritdoc />
    public override void SetLength(long value) => throw new NotSupportedException("The view is forward-only.");

    // 不关闭调用方的流：视图只是一层转发。
    protected override void Dispose(bool disposing)
    {
    }
}
