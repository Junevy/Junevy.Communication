namespace Junevy.Communication.DebugAssistant.Protocols.Channels.Settings;

/// <summary>
/// 通道参数编辑器的保存内容：每个编辑器一节（键为节名，值为属性名到文本的映射）。
/// 节名固定：framing、correlation、heartbeat、reconnect、timeouts、socket、tls、handshake。
/// </summary>
public sealed record ChannelSettingsValues(Dictionary<string, Dictionary<string, string>> Sections);
