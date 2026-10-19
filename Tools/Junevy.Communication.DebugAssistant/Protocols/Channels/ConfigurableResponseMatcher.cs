using Junevy.Communication.Channels;

namespace Junevy.Communication.DebugAssistant.Protocols.Channels;

/// <summary>
/// 按界面配置的规则匹配应答（计划 DA-D12）。越界或长度不足时返回不匹配，从不抛出异常。
/// </summary>
internal sealed class ConfigurableResponseMatcher : IResponseMatcher
{
    private readonly ResponseMatchRule _rule;

    public ConfigurableResponseMatcher(ResponseMatchRule rule)
    {
        ArgumentNullException.ThrowIfNull(rule);
        _rule = rule switch
        {
            PrefixMatchRule { Prefix.Length: 0 } => throw new ArgumentException("The response prefix must not be empty.", nameof(rule)),
            EchoMatchRule { Offset: < 0 } or EchoMatchRule { Length: <= 0 } =>
                throw new ArgumentException("The echo offset must not be negative and the length must be positive.", nameof(rule)),
            _ => rule,
        };
    }

    public bool IsMatch(ReadOnlySpan<byte> request, ReadOnlySpan<byte> frame) => _rule switch
    {
        PrefixMatchRule prefix => frame.StartsWith(prefix.Prefix),
        EchoMatchRule echo => Echoes(echo, request, frame),
        _ => false,
    };

    private static bool Echoes(EchoMatchRule echo, ReadOnlySpan<byte> request, ReadOnlySpan<byte> frame)
    {
        int end = echo.Offset + echo.Length;
        return request.Length >= end
            && frame.Length >= end
            && request.Slice(echo.Offset, echo.Length).SequenceEqual(frame.Slice(echo.Offset, echo.Length));
    }
}
