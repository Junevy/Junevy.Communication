using Junevy.Communication.DebugAssistant.Sessions;
using Junevy.Communication.DebugAssistant.Shell;
using Junevy.Communication.DebugAssistant.Workspace;

namespace Junevy.Communication.DebugAssistant.Tests.Support;

/// <summary>连接与断开的行为由测试脚本决定的页签，用来验证基类的命令、状态与操作记录。</summary>
internal sealed class ScriptedSession : SessionViewModel
{
    private readonly Func<ScriptedSession, CancellationToken, Task> _connect;
    private readonly Func<ScriptedSession, CancellationToken, Task> _disconnect;

    public ScriptedSession(
        SessionServices dependencies,
        Func<ScriptedSession, CancellationToken, Task>? connect = null,
        Func<ScriptedSession, CancellationToken, Task>? disconnect = null)
        : base(ProtocolCatalog.Get(ProtocolKind.Modbus), dependencies)
    {
        _connect = connect ?? ((_, _) => Task.CompletedTask);
        _disconnect = disconnect ?? ((_, _) => Task.CompletedTask);
    }

    public void SetState(SessionState state) => State = state;

    public override SessionSettings CaptureSettings() => new PlaceholderSettings();

    public override void ApplySettings(SessionSettings settings)
    {
    }

    public void Record(OperationRecord record) => RecordOperation(record);

    public void Post(Action action) => PostToUi(action);

    protected override Task ConnectCoreAsync(CancellationToken cancellationToken) => _connect(this, cancellationToken);

    protected override Task DisconnectCoreAsync(CancellationToken cancellationToken) => _disconnect(this, cancellationToken);
}
