using System.Buffers;
using Junevy.Communication.Channels;
using Junevy.Communication.Testing;

namespace Junevy.Communication.Serial.Tests;

/// <summary>
/// 模拟串口设备（设备一侧的流）：持续读取请求帧，每帧经 <paramref name="replyDelayMilliseconds"/> 毫秒后回复。
/// 与单线程的 <see cref="DeviceSimulator"/> 不同，读取与回复并行进行，因此能观察到通道是否同时发出了多个请求：
/// <see cref="MaxOutstanding"/> 记录"已收到但尚未回复"的最大数量，串行的 Sequential 通道应为 1。
/// </summary>
internal sealed class SerialDevice
{
    private readonly Stream stream;
    private readonly IFrameDecoder decoder;
    private readonly IFrameEncoder encoder;
    private readonly Func<byte[], byte[]> reply;
    private readonly int replyDelayMilliseconds;
    private readonly SemaphoreSlim writeGate = new SemaphoreSlim(1, 1);
    private int outstanding;
    private int maxOutstanding;
    private int received;

    /// <summary>创建模拟设备。</summary>
    /// <param name="stream">设备一侧的流。</param>
    /// <param name="codec">与通道一致的分帧规则。</param>
    /// <param name="reply">根据请求生成应答负载。</param>
    /// <param name="replyDelayMilliseconds">收到请求后等待多久再回复（毫秒）。</param>
    public SerialDevice(Stream stream, IFrameCodecFactory codec, Func<byte[], byte[]> reply, int replyDelayMilliseconds)
    {
        this.stream = stream;
        decoder = codec.CreateDecoder();
        encoder = codec.CreateEncoder();
        this.reply = reply;
        this.replyDelayMilliseconds = replyDelayMilliseconds;
    }

    /// <summary>收到的请求帧数。</summary>
    public int Received => Volatile.Read(ref received);

    /// <summary>同时在途（已收到、尚未回复）的最大数量。</summary>
    public int MaxOutstanding => Volatile.Read(ref maxOutstanding);

    /// <summary>开始在后台读取请求，直到取消或流结束。</summary>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>读取循环结束的任务。</returns>
    public Task RunAsync(CancellationToken cancellationToken) => ReadLoopAsync(cancellationToken);

    private async Task ReadLoopAsync(CancellationToken token)
    {
        var chunk = new byte[256];
        byte[] pending = new byte[1024];
        int count = 0;
        try
        {
            while (true)
            {
                int read = await stream.ReadAsync(chunk, 0, chunk.Length, token).ConfigureAwait(false);
                if (read == 0)
                    return;

                if (count + read > pending.Length)
                    Array.Resize(ref pending, Math.Max(pending.Length * 2, count + read));

                Buffer.BlockCopy(chunk, 0, pending, count, read);
                count += read;

                var buffer = new ReadOnlySequence<byte>(pending, 0, count);
                while (decoder.TryDecode(ref buffer, out ReadOnlySequence<byte> frame))
                    Accept(frame.ToArray(), token);

                int remaining = (int)buffer.Length;
                Buffer.BlockCopy(pending, count - remaining, pending, 0, remaining);
                count = remaining;
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            // 测试结束：正常退出。
        }
        catch (IOException)
        {
            // 链路中断：设备掉线。
        }
    }

    // 收到一帧：先计入在途数量，再安排延迟回复。
    private void Accept(byte[] request, CancellationToken token)
    {
        Interlocked.Increment(ref received);
        RecordMaxOutstanding(Interlocked.Increment(ref outstanding));
        _ = ReplyAsync(request, token);
    }

    private async Task ReplyAsync(byte[] request, CancellationToken token)
    {
        try
        {
            await Task.Delay(replyDelayMilliseconds, token).ConfigureAwait(false);
            var output = new ByteArrayBufferWriter();
            encoder.Encode(reply(request), output);
            byte[] wire = output.ToArray();

            // 先减少在途数量，再写出应答：客户端只有收到应答后才会发出下一个请求，因此计数不会被后续请求误计。
            Interlocked.Decrement(ref outstanding);

            await writeGate.WaitAsync(token).ConfigureAwait(false);
            try
            {
                await stream.WriteAsync(wire, 0, wire.Length, token).ConfigureAwait(false);
                await stream.FlushAsync(token).ConfigureAwait(false);
            }
            finally
            {
                writeGate.Release();
            }
        }
        catch (OperationCanceledException)
        {
            // 测试结束：放弃未发出的应答。
        }
        catch (IOException)
        {
            // 链路中断：应答无法发出。
        }
    }

    private void RecordMaxOutstanding(int value)
    {
        int seen;
        do
        {
            seen = Volatile.Read(ref maxOutstanding);
            if (value <= seen)
                return;
        }
        while (Interlocked.CompareExchange(ref maxOutstanding, value, seen) != seen);
    }
}
