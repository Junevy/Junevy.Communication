namespace Junevy.Communication.DebugAssistant.Protocols.Channels;

/// <summary>应答匹配规则（计划 DA-D12）：界面上配置，<see cref="ConfigurableResponseMatcher"/> 按它判断应答属于哪个请求。</summary>
public abstract record ResponseMatchRule;

/// <summary>应答以某段字节开头。</summary>
public sealed record PrefixMatchRule(byte[] Prefix) : ResponseMatchRule;

/// <summary>应答与请求在偏移 <see cref="Offset"/> 处的 <see cref="Length"/> 个字节相同（设备回显）。</summary>
public sealed record EchoMatchRule(int Offset, int Length) : ResponseMatchRule;
