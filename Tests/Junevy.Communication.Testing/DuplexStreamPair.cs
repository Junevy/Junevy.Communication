using System.Buffers;
using System.IO.Pipelines;

namespace Junevy.Communication.Testing;

/// <summary>
/// 由两个内存管道组成的双工流对：A 写入的字节由 B 读取，B 写入的字节由 A 读取。
/// 用于在不使用套接字的情况下驱动 StreamChannel 与 <see cref="DeviceSimulator"/>。
/// </summary>
/// <remarks>
/// 只实现 <c>byte[]</c> 重载，在 net8.0 上 <c>Span</c> / <c>Memory</c> 重载由基类转发到它们，因此两个目标框架的行为一致。
/// 读写依赖管道的取消（<c>CancelPendingRead</c> / <c>CancelPendingFlush</c>）唤醒挂起的操作，因此 <see cref="Abort"/> 可以立即打断它们。
/// </remarks>
public sealed class DuplexStreamPair : IDisposable
{
    private readonly Pipe aToB;
    private readonly Pipe bToA;
    private int aborted;
    private int disposed;

    private DuplexStreamPair(long pauseWriterThreshold)
    {
        var options = new PipeOptions(
            pauseWriterThreshold: pauseWriterThreshold,
            resumeWriterThreshold: pauseWriterThreshold / 2,
            useSynchronizationContext: false);

        aToB = new Pipe(options);
        bToA = new Pipe(options);
        A = new EndpointStream(this, bToA.Reader, aToB.Writer);
        B = new EndpointStream(this, aToB.Reader, bToA.Writer);
    }

    /// <summary>端点 A：写入的字节由 B 读取，读取的字节来自 B。</summary>
    public Stream A { get; }

    /// <summary>端点 B：写入的字节由 A 读取，读取的字节来自 A。</summary>
    public Stream B { get; }

    /// <summary>
    /// 创建一对双工流。
    /// </summary>
    /// <param name="pauseWriterThreshold">某一方向上未被读取的字节超过此值时，写入挂起（模拟"对端不读取"导致的发送阻塞）；必须为正。</param>
    /// <returns>新的流对。</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="pauseWriterThreshold"/> 不为正。</exception>
    public static DuplexStreamPair Create(long pauseWriterThreshold = 1024 * 1024)
    {
        if (pauseWriterThreshold <= 0)
            throw new ArgumentOutOfRangeException(nameof(pauseWriterThreshold), pauseWriterThreshold, "The pause threshold must be positive.");

        return new DuplexStreamPair(pauseWriterThreshold);
    }

    /// <summary>
    /// 模拟链路中断：唤醒两端挂起的读写，此后两端的读写都抛出 <see cref="IOException"/>。可重复调用。
    /// </summary>
    public void Abort()
    {
        if (Interlocked.Exchange(ref aborted, 1) != 0)
            return;

        aToB.Reader.CancelPendingRead();
        bToA.Reader.CancelPendingRead();
        aToB.Writer.CancelPendingFlush();
        bToA.Writer.CancelPendingFlush();
    }

    /// <summary>中断链路。之后两端的读写都抛出 <see cref="IOException"/>。</summary>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0)
            return;

        Abort();
    }

    private bool IsBroken => Volatile.Read(ref aborted) != 0 || Volatile.Read(ref disposed) != 0;

    /// <summary>
    /// 一个端点的流视图：读取一个管道，写入另一个管道。
    /// </summary>
    private sealed class EndpointStream : Stream
    {
        private readonly DuplexStreamPair owner;
        private readonly PipeReader reader;
        private readonly PipeWriter writer;
        private int closed;

        public EndpointStream(DuplexStreamPair owner, PipeReader reader, PipeWriter writer)
        {
            this.owner = owner;
            this.reader = reader;
            this.writer = writer;
        }

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => true;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override void Flush()
        {
            // 写入在 WriteAsync 中已经提交到管道，没有需要刷新的内部缓冲。
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override int Read(byte[] buffer, int offset, int count)
            => ReadAsync(buffer, offset, count, CancellationToken.None).GetAwaiter().GetResult();

        public override void Write(byte[] buffer, int offset, int count)
            => WriteAsync(buffer, offset, count, CancellationToken.None).GetAwaiter().GetResult();

        public override async Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        {
            ValidateArguments(buffer, offset, count);
            ThrowIfUnavailable();
            if (count == 0)
                return 0;

            ReadResult result = await reader.ReadAsync(cancellationToken).ConfigureAwait(false);
            ThrowIfUnavailable();
            if (result.IsCanceled)
                throw new IOException("The duplex stream was aborted.");

            ReadOnlySequence<byte> available = result.Buffer;
            if (available.Length == 0)
            {
                // 写入端已完成：读到流末尾。
                reader.AdvanceTo(available.End);
                return 0;
            }

            int toCopy = (int)Math.Min(count, available.Length);
            ReadOnlySequence<byte> slice = available.Slice(0, toCopy);
            CopyTo(slice, buffer, offset);

            // 只消费已复制的部分；剩余数据保持未检查，下次读取可以立即返回。
            reader.AdvanceTo(slice.End);
            return toCopy;
        }

        public override async Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        {
            ValidateArguments(buffer, offset, count);
            ThrowIfUnavailable();
            if (count == 0)
                return;

            // 超过暂停阈值时此处挂起，直到对端读取；Abort 通过 CancelPendingFlush 唤醒。
            FlushResult result = await writer.WriteAsync(new ReadOnlyMemory<byte>(buffer, offset, count), cancellationToken)
                .ConfigureAwait(false);

            // 对端已经关闭读取，或链路被中断，都视为链路不可用。
            if (result.IsCanceled || result.IsCompleted || owner.IsBroken)
                throw new IOException("The duplex stream was closed.");
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing && Interlocked.Exchange(ref closed, 1) == 0)
            {
                // 对端读到流末尾；对端之后的写入会失败。
                writer.Complete();
                reader.Complete();
            }

            base.Dispose(disposing);
        }

        private void ThrowIfUnavailable()
        {
            if (Volatile.Read(ref closed) != 0)
                throw new ObjectDisposedException(GetType().Name);

            if (owner.IsBroken)
                throw new IOException("The duplex stream is closed.");
        }

        private static void ValidateArguments(byte[] buffer, int offset, int count)
        {
            if (buffer == null)
                throw new ArgumentNullException(nameof(buffer));

            if (offset < 0 || count < 0 || offset > buffer.Length - count)
                throw new ArgumentOutOfRangeException(nameof(count), "Offset and count must describe a range within the buffer.");
        }

        private static void CopyTo(ReadOnlySequence<byte> source, byte[] destination, int offset)
        {
            int written = 0;
            SequencePosition position = source.Start;
            while (source.TryGet(ref position, out ReadOnlyMemory<byte> memory))
            {
                ReadOnlySpan<byte> span = memory.Span;
                span.CopyTo(new Span<byte>(destination, offset + written, span.Length));
                written += span.Length;
            }
        }
    }
}
