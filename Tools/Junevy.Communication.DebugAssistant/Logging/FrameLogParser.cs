using System.Diagnostics.CodeAnalysis;
using System.Text.RegularExpressions;

namespace Junevy.Communication.DebugAssistant.Logging;

/// <summary>
/// 从库的 Debug 日志中识别报文。模板必须与库的写法逐字一致（Channels README 的 "Logging" 一节）。
/// 测试锁定这些模板：库改了模板，测试失败，而不是报文悄悄不再显示（DA-D5）。
/// </summary>
internal static partial class FrameLogParser
{
    internal const string StreamTx = "TX {Length} bytes: {Hex}";
    internal const string StreamRx = "RX {Length} bytes: {Hex}";
    internal const string DatagramTx = "TX {Length} bytes to {Destination}: {Hex}";
    internal const string DatagramRx = "RX {Length} bytes from {Remote}: {Hex}";

    private const string OriginalFormatKey = "{OriginalFormat}";

    private static readonly Dictionary<string, FrameShape> Shapes = new()
    {
        [StreamTx] = new FrameShape(FrameDirection.Tx, EndpointKey: null),
        [StreamRx] = new FrameShape(FrameDirection.Rx, EndpointKey: null),
        [DatagramTx] = new FrameShape(FrameDirection.Tx, EndpointKey: "Destination"),
        [DatagramRx] = new FrameShape(FrameDirection.Rx, EndpointKey: "Remote"),
    };

    [GeneratedRegex(@"\.\.\.\(\+\d+ bytes\)$")]
    private static partial Regex TruncationSuffix();

    /// <summary>如果 <paramref name="state"/> 是报文日志，返回对应的 <see cref="FrameEntry"/>。</summary>
    public static bool TryParse<TState>(TState state, DateTimeOffset time, [NotNullWhen(true)] out FrameEntry? frame)
    {
        frame = null;
        if (state is not IReadOnlyList<KeyValuePair<string, object?>> values
            || Find(values, OriginalFormatKey) is not string template
            || !Shapes.TryGetValue(template, out FrameShape? shape))
        {
            return false;
        }

        string hex = Find(values, "Hex")?.ToString() ?? string.Empty;
        int length = Find(values, "Length") is int declared ? declared : 0;
        string? endpoint = shape.EndpointKey is null ? null : Find(values, shape.EndpointKey)?.ToString();
        frame = new FrameEntry(time, shape.Direction, length, hex, TruncationSuffix().IsMatch(hex), endpoint);
        return true;
    }

    private sealed record FrameShape(FrameDirection Direction, string? EndpointKey);

    private static object? Find(IReadOnlyList<KeyValuePair<string, object?>> values, string key)
    {
        foreach (KeyValuePair<string, object?> pair in values)
        {
            if (pair.Key == key)
            {
                return pair.Value;
            }
        }

        return null;
    }
}
