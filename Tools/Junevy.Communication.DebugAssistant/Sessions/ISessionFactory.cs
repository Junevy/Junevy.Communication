using Junevy.Communication.DebugAssistant.Shell;

namespace Junevy.Communication.DebugAssistant.Sessions;

/// <summary>按页签种类创建新的调试页签。</summary>
public interface ISessionFactory
{
    /// <summary>创建一个新页签，标题为该协议的默认名称（"&lt;ShortName&gt; #n"）。</summary>
    SessionViewModel Create(ProtocolKind kind);
}
