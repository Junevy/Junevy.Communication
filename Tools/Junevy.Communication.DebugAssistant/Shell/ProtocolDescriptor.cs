namespace Junevy.Communication.DebugAssistant.Shell;

/// <summary>
/// 一种页签的描述：树上的标题、默认页签名的前缀（"&lt;ShortName&gt; #n"）与图标字形（iconfont 码点）。
/// </summary>
public sealed record ProtocolDescriptor(ProtocolKind Kind, ProtocolGroup Group, string Title, string ShortName, string Glyph);
