using Junevy.Communication.DebugAssistant.Shell;

namespace Junevy.Communication.DebugAssistant.Sessions;

/// <summary>默认页签名："&lt;ShortName&gt; #n"，n 按协议各自递增（计划 DA-D8）。名称不要求唯一。</summary>
internal sealed class SessionNamer
{
    private readonly Dictionary<ProtocolKind, int> _lastNumber = [];

    public string Next(ProtocolDescriptor descriptor)
    {
        int number = _lastNumber.GetValueOrDefault(descriptor.Kind) + 1;
        _lastNumber[descriptor.Kind] = number;
        return $"{descriptor.ShortName} #{number}";
    }
}
