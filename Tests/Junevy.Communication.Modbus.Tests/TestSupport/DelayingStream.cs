namespace Junevy.Communication.Modbus.Tests;

/// <summary>
/// 写入前按需延迟的流（测试专用）：每次 <see cref="WriteAsync(byte[], int, int, CancellationToken)"/> 询问一次延迟毫秒数。
/// 延迟的写入在后台完成，调用方（设备的分帧循环）立即返回，因此后续的请求仍被处理，回复可以乱序到达。写入之间用门串行化。
/// </summary>
internal sealed class DelayingStream : Stream
{
    private readonly Stream inner;
    private readonly Func<int> delayForWrite;
    private readonly SemaphoreSlim gate = new SemaphoreSlim(1, 1);

    /// <summary>包装一个流。</summary>
    /// <param name="inner">底层流。</param>
    /// <param name="delayForWrite">每次写入询问的延迟毫秒数。</param>
    public DelayingStream(Stream inner, Func<int> delayForWrite)
    {
        this.inner = inner;
        this.delayForWrite = delayForWrite;
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
    public override void Flush() => inner.Flush();

    /// <inheritdoc />
    public override int Read(byte[] buffer, int offset, int count) => inner.Read(buffer, offset, count);

    /// <inheritdoc />
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    /// <inheritdoc />
    public override void SetLength(long value) => throw new NotSupportedException();

    /// <inheritdoc />
    public override void Write(byte[] buffer, int offset, int count) => inner.Write(buffer, offset, count);

    /// <inheritdoc />
    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        => inner.ReadAsync(buffer, offset, count, cancellationToken);

    /// <inheritdoc />
    public override Task FlushAsync(CancellationToken cancellationToken) => inner.FlushAsync(cancellationToken);

    /// <inheritdoc />
    public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
    {
        byte[] copy = new byte[count];
        Array.Copy(buffer, offset, copy, 0, count);

        int delay = delayForWrite();
        if (delay <= 0)
            return WriteNowAsync(copy, cancellationToken);

        _ = WriteLaterAsync(copy, delay);
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            inner.Dispose();
            gate.Dispose();
        }

        base.Dispose(disposing);
    }

    private async Task WriteNowAsync(byte[] bytes, CancellationToken cancellationToken)
    {
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await inner.WriteAsync(bytes, 0, bytes.Length, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            gate.Release();
        }
    }

    private async Task WriteLaterAsync(byte[] bytes, int delay)
    {
        try
        {
            await Task.Delay(delay).ConfigureAwait(false);
            await WriteNowAsync(bytes, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception)
        {
            // 连接已经关闭（客户端超时后重连）：迟到的回复被丢弃。
        }
    }
}
