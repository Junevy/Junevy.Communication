using Junevy.Communication.Core.Results;

namespace Junevy.Communication.Melsec.Protocol;

/// <summary>
/// 应答数据部分的长度校验与规范化（3E / 4E 与 4C 共用）：按数据种类与点数检查长度，并转换为规范化布局（P2-D5）。
/// </summary>
internal static class McResponseData
{
    /// <summary>读取应答的数据部分（结束码之后、校验之前）。</summary>
    /// <param name="codec">字段编码策略。</param>
    /// <param name="data">数据部分。</param>
    /// <param name="kind">数据种类。</param>
    /// <param name="count">字数、点数或回环字节数。</param>
    /// <returns>解码结果；长度或内容不符为 ProtocolViolation。</returns>
    public static CommResult<McResponse> Read(McFieldCodec codec, ReadOnlySpan<byte> data, McDataKind kind, int count) => kind switch
    {
        McDataKind.None => data.Length == 0
            ? McResponse.Decoded(0, Array.Empty<byte>())
            : McResponse.Violation("The MC write response must not contain data."),
        McDataKind.Words => ReadWords(codec, data, count),
        McDataKind.Bits => ReadBits(codec, data, count),
        McDataKind.Loopback => ReadLoopback(codec, data, count),
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "The MC data kind is not defined."),
    };

    private static CommResult<McResponse> ReadWords(McFieldCodec codec, ReadOnlySpan<byte> data, int count)
    {
        if (data.Length != codec.WordFieldSize(count))
            return McResponse.Violation("The MC response word data length does not match the requested word count.");

        var words = new byte[count * 2];
        return codec.TryReadWords(data, words)
            ? McResponse.Decoded(0, words)
            : McResponse.Violation("The MC response word data is malformed.");
    }

    private static CommResult<McResponse> ReadBits(McFieldCodec codec, ReadOnlySpan<byte> data, int count)
    {
        if (data.Length != codec.BitFieldSize(count))
            return McResponse.Violation("The MC response bit data length does not match the requested point count.");

        var points = new byte[count];
        return codec.TryReadBits(data, points)
            ? McResponse.Decoded(0, points)
            : McResponse.Violation("The MC response bit data is malformed.");
    }

    private static CommResult<McResponse> ReadLoopback(McFieldCodec codec, ReadOnlySpan<byte> data, int count)
    {
        if (!codec.TryTakeUInt16(ref data, out ushort echoedCount) || echoedCount != count || data.Length != count)
            return McResponse.Violation("The MC loopback response does not echo the request.");

        return McResponse.Decoded(0, data.ToArray());
    }
}
