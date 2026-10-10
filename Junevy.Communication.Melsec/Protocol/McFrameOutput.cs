using System.Buffers;

namespace Junevy.Communication.Melsec.Protocol;

/// <summary>把编码完成的帧复制到调用方的缓冲写入器（3E / 4E 与 4C 共用）。</summary>
internal static class McFrameOutput
{
    /// <summary>复制 <paramref name="frame"/> 中已写入的全部字节到 <paramref name="output"/>。</summary>
    /// <param name="frame">已编码的帧。</param>
    /// <param name="output">目标缓冲写入器。</param>
    public static void CopyTo(MemoryStream frame, IBufferWriter<byte> output)
    {
        int length = (int)frame.Length;
        new ReadOnlySpan<byte>(frame.GetBuffer(), 0, length).CopyTo(output.GetSpan(length));
        output.Advance(length);
    }
}
