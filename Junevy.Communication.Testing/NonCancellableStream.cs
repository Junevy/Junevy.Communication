namespace Junevy.Communication.Testing;

/// <summary>
/// 忽略取消令牌的流包装：读取、写入与刷新都以 <see cref="CancellationToken.None"/> 调用内部流。
/// 用于模拟 net472 上 <c>NetworkStream</c> 与 <c>SerialPort.BaseStream</c> 不响应取消的行为（设计文档 6.2 节，计划 D7）：
/// 在这种流上，超时、停止与半帧计时只能依靠 abortTransport 打断挂起的 I/O。
/// <see cref="Dispose(bool)"/> 时释放内部流。
/// </summary>
public sealed class NonCancellableStream : Stream
{
    private readonly Stream inner;

    /// <summary>
    /// 包装内部流。
    /// </summary>
    /// <param name="inner">内部流；不能为 null。</param>
    /// <exception cref="ArgumentNullException"><paramref name="inner"/> 为 null。</exception>
    public NonCancellableStream(Stream inner)
    {
        this.inner = inner ?? throw new ArgumentNullException(nameof(inner));
    }

    /// <inheritdoc />
    public override bool CanRead => inner.CanRead;

    /// <inheritdoc />
    public override bool CanSeek => inner.CanSeek;

    /// <inheritdoc />
    public override bool CanWrite => inner.CanWrite;

    /// <inheritdoc />
    public override long Length => inner.Length;

    /// <inheritdoc />
    public override long Position
    {
        get => inner.Position;
        set => inner.Position = value;
    }

    /// <inheritdoc />
    public override void Flush() => inner.Flush();

    /// <inheritdoc />
    public override Task FlushAsync(CancellationToken cancellationToken) => inner.FlushAsync(CancellationToken.None);

    /// <inheritdoc />
    public override int Read(byte[] buffer, int offset, int count) => inner.Read(buffer, offset, count);

    /// <inheritdoc />
    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        => inner.ReadAsync(buffer, offset, count, CancellationToken.None);

    /// <inheritdoc />
    public override void Write(byte[] buffer, int offset, int count) => inner.Write(buffer, offset, count);

    /// <inheritdoc />
    public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        => inner.WriteAsync(buffer, offset, count, CancellationToken.None);

    /// <inheritdoc />
    public override long Seek(long offset, SeekOrigin origin) => inner.Seek(offset, origin);

    /// <inheritdoc />
    public override void SetLength(long value) => inner.SetLength(value);

    /// <inheritdoc />
    protected override void Dispose(bool disposing)
    {
        if (disposing)
            inner.Dispose();

        base.Dispose(disposing);
    }
}
