using System.Buffers;

namespace Junevy.Communication.Melsec.Protocol;

/// <summary>把编码完成的帧写入调用方的缓冲写入器（3E / 4E 与 4C 共用）。</summary>
internal static class McFrameOutput
{
    /// <summary>把整帧复制到 <paramref name="output"/>。</summary>
    /// <param name="frame">已编码的帧。</param>
    /// <param name="output">目标缓冲写入器。</param>
    public static void Write(byte[] frame, IBufferWriter<byte> output)
    {
        new ReadOnlySpan<byte>(frame).CopyTo(output.GetSpan(frame.Length));
        output.Advance(frame.Length);
    }
}
