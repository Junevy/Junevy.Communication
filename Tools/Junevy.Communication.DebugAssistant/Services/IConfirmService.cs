namespace Junevy.Communication.DebugAssistant.Services;

/// <summary>危险操作前的确认（WPF 实现位于 Views/Platform）。</summary>
public interface IConfirmService
{
    /// <summary>询问用户；返回 true 表示用户确认。</summary>
    Task<bool> ConfirmAsync(string title, string message);
}
