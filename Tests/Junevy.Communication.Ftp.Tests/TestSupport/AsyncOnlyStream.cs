namespace Junevy.Communication.Ftp.Tests.TestSupport;

/// <summary>
/// 只支持异步读写、不可定位的流：同步的 <c>Read</c> 与 <c>Write</c> 抛 <see cref="InvalidOperationException"/>，
/// 与 ASP.NET Core 默认禁止同步 IO 的请求体、响应体相同（P6.3 审阅 R2）。
/// </summary>
public sealed class AsyncOnlyStream : Stream
{
    private readonly Stream inner;

    /// <summary>包住一个内存中的流。</summary>
    /// <param name="inner">实际保存数据的流。</param>
    public AsyncOnlyStream(Stream inner)
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
    public override long Length => throw new NotSupportedException();

    /// <inheritdoc />
    public override long Position
    {
        get => throw new NotSupportedException();
        set => throw new NotSupportedException();
    }

    /// <inheritdoc />
    public override void Flush()
    {
    }

    /// <inheritdoc />
    public override Task FlushAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    /// <inheritdoc />
    public override int Read(byte[] buffer, int offset, int count) => throw SynchronousIo();

    /// <inheritdoc />
    public override int Read(Span<byte> buffer) => throw SynchronousIo();

    /// <inheritdoc />
    public override void Write(byte[] buffer, int offset, int count) => throw SynchronousIo();

    /// <inheritdoc />
    public override void Write(ReadOnlySpan<byte> buffer) => throw SynchronousIo();

    /// <inheritdoc />
    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
        inner.ReadAsync(buffer, offset, count, cancellationToken);

    /// <inheritdoc />
    public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
        inner.ReadAsync(buffer, cancellationToken);

    /// <inheritdoc />
    public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
        inner.WriteAsync(buffer, offset, count, cancellationToken);

    /// <inheritdoc />
    public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default) =>
        inner.WriteAsync(buffer, cancellationToken);

    /// <inheritdoc />
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    /// <inheritdoc />
    public override void SetLength(long value) => throw new NotSupportedException();

    private static InvalidOperationException SynchronousIo() => new("Synchronous operations are disallowed.");
}
