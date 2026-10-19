using Junevy.Communication.DebugAssistant.Shell;

namespace Junevy.Communication.DebugAssistant.Sessions;

/// <summary>尚未实现的协议页签占位。每种协议的真实页签完成后被替换，Task 19 结束时删除。</summary>
public sealed class PlaceholderSessionViewModel : SessionViewModel
{
    public PlaceholderSessionViewModel(ProtocolDescriptor descriptor, SessionServices dependencies)
        : base(descriptor, dependencies)
    {
    }
}
