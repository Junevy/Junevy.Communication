using System.Buffers;
using System.IO.Pipelines;

namespace Junevy.Communication.Channels.Tests;

/// <summary>
/// 分帧器的喂入结果：交出的全部帧，以及 <c>TryDecode</c> 被调用的次数。
/// </summary>
internal sealed class FeedResult
{
    public FeedResult(IReadOnlyList<byte[]> frames, int tryDecodeCalls)
    {
        Frames = frames;
        TryDecodeCalls = tryDecodeCalls;
    }

    /// <summary>交出的帧（每帧为独立副本）。</summary>
    public IReadOnlyList<byte[]> Frames { get; }

    /// <summary>分帧器 <c>TryDecode</c> 的调用次数。</summary>
    public int TryDecodeCalls { get; }
}

/// <summary>
/// 测试辅助：模拟字节流的到达方式。数据经由 <see cref="Pipe"/> 逐块写入（与 StreamChannel 的内部管道一致），
/// 每块到达后立即调用分帧器，因此分帧器看到的是真实的"不完整数据"。
/// </summary>
internal static class DecoderHarness
{
    /// <summary>
    /// 按 <paramref name="chunkSize"/> 逐块喂入：每块到达后循环调用 <c>TryDecode</c> 直到返回 false；
    /// 全部数据到达后，若分帧器可刷新，则调用 <c>TryFlush</c> 交出残余数据（相当于静默超时）。
    /// </summary>
    /// <param name="decoder">分帧器。</param>
    /// <param name="stream">线路上的全部字节。</param>
    /// <param name="chunkSize">每块的字节数，必须为正。</param>
    /// <returns>交出的帧与调用次数。</returns>
    public static FeedResult Feed(IFrameDecoder decoder, byte[] stream, int chunkSize)
    {
        if (chunkSize <= 0)
            throw new ArgumentOutOfRangeException(nameof(chunkSize), chunkSize, "Chunk size must be positive.");

        var pipe = new Pipe(new PipeOptions(pauseWriterThreshold: 1 << 20, resumeWriterThreshold: 1 << 19, useSynchronizationContext: false));
        var frames = new List<byte[]>();
        int calls = 0;

        for (int offset = 0; offset < stream.Length; offset += chunkSize)
        {
            int count = Math.Min(chunkSize, stream.Length - offset);
            pipe.Writer.WriteAsync(new ReadOnlyMemory<byte>(stream, offset, count)).GetAwaiter().GetResult();
            Drain(decoder, pipe.Reader, frames, ref calls);
        }

        if (decoder is IFlushableFrameDecoder flushable)
            Flush(flushable, pipe.Reader, frames);

        pipe.Writer.Complete();
        pipe.Reader.Complete();
        return new FeedResult(frames, calls);
    }

    /// <summary>
    /// 对一个完整的（可能多段的）序列循环分帧，然后刷新（如果支持）。用于验证多段缓冲的切分结果。
    /// </summary>
    /// <param name="decoder">分帧器。</param>
    /// <param name="buffer">完整的序列。</param>
    /// <returns>交出的帧。</returns>
    public static IReadOnlyList<byte[]> DecodeAll(IFrameDecoder decoder, ReadOnlySequence<byte> buffer)
    {
        var frames = new List<byte[]>();
        while (decoder.TryDecode(ref buffer, out ReadOnlySequence<byte> frame))
            frames.Add(frame.ToArray());

        if (decoder is IFlushableFrameDecoder flushable)
        {
            while (flushable.TryFlush(ref buffer, out ReadOnlySequence<byte> remaining))
                frames.Add(remaining.ToArray());
        }

        return frames;
    }

    private static void Drain(IFrameDecoder decoder, PipeReader reader, List<byte[]> frames, ref int calls)
    {
        if (!reader.TryRead(out ReadResult result))
            return;

        ReadOnlySequence<byte> buffer = result.Buffer;
        while (true)
        {
            calls++;
            if (!decoder.TryDecode(ref buffer, out ReadOnlySequence<byte> frame))
                break;

            frames.Add(frame.ToArray());
        }

        // 未成帧的数据仍视为"未检查"，下一块到达时会再次出现在缓冲中。
        reader.AdvanceTo(buffer.Start, buffer.Start);
    }

    private static void Flush(IFlushableFrameDecoder decoder, PipeReader reader, List<byte[]> frames)
    {
        if (!reader.TryRead(out ReadResult result))
            return;

        ReadOnlySequence<byte> buffer = result.Buffer;
        while (decoder.TryFlush(ref buffer, out ReadOnlySequence<byte> frame))
            frames.Add(frame.ToArray());

        reader.AdvanceTo(buffer.End);
    }
}
