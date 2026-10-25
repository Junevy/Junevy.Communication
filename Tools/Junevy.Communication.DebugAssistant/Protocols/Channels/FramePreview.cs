using System.Buffers;
using Junevy.Communication.Channels;
using Junevy.Communication.Channels.Framing;

namespace Junevy.Communication.DebugAssistant.Protocols.Channels;

/// <summary>分帧预览（计划第 Task 4 节）：用库的分帧器把一段字节切成帧，不需要连接。</summary>
public static class FramePreview
{
    /// <summary>预览结果：切出的帧、剩余未成帧的字节数，以及需要告诉用户的说明。</summary>
    public sealed record Result(IReadOnlyList<byte[]> Frames, int LeftoverBytes, string? Notice);

    public static Result Decode(FramingOptions options, byte[] input)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(input);
        if (options.Mode == FramingMode.IdleGap)
        {
            return new Result([], 0, "空闲间隔按时间切分，预览无法模拟，请在连接后观察。");
        }

        try
        {
            IFrameDecoder decoder = FrameCodecFactory.Create(options).CreateDecoder();
            var buffer = new ReadOnlySequence<byte>(input);
            var frames = new List<byte[]>();
            while (decoder.TryDecode(ref buffer, out ReadOnlySequence<byte> frame))
            {
                frames.Add(frame.ToArray());
            }

            return new Result(frames, (int)buffer.Length, buffer.Length > 0 ? $"剩余 {buffer.Length} 字节未组成完整的帧。" : null);
        }
        catch (Exception ex) when (ex is ArgumentException or FormatException or InvalidOperationException or Junevy.Communication.Channels.Framing.FrameDecodeException)
        {
            return new Result([], input.Length, ex.Message);
        }
    }
}
