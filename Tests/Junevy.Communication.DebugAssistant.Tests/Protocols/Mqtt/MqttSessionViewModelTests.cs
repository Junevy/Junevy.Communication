using Junevy.Communication.Core.Results;
using Junevy.Communication.DebugAssistant.Protocols.Mqtt;
using Junevy.Communication.DebugAssistant.Sessions;
using Junevy.Communication.DebugAssistant.Shell;
using Junevy.Communication.DebugAssistant.Tests.Support;
using Junevy.Communication.Mqtt;

namespace Junevy.Communication.DebugAssistant.Tests.Protocols.Mqtt;

/// <summary>
/// MQTT 页的视图模型，不连接网络（计划 Task 17 第 2 条的单元部分）：默认值、设置往返（密码不保存）、连接参数被拒绝、
/// 未连接时命令不可用、用户属性的增删、V3.1.1 与 V5.0 的选项切换。网络行为见 <c>MqttEndToEndTests</c>。
/// </summary>
public sealed class MqttSessionViewModelTests
{
    private static MqttSessionViewModel CreatePage() =>
        new(ProtocolCatalog.Get(ProtocolKind.Mqtt), FakeServices.Create(new ManualUiDispatcher()));

    [Fact]
    public async Task Page_StartsNotConnected_WithTheLibraryDefaults()
    {
        await using var page = CreatePage();

        Assert.Equal(("未连接", SessionState.NotConnected, true, false), (page.MqttStateText, page.State, page.IsEditable, page.IsLinked));
        Assert.Equal((MqttProtocolVersion.V311, 1883, true), (page.ProtocolVersion, page.Port, page.AutoReconnect));
        Assert.Equal((true, false), (page.IsVersion311, page.IsVersion500));
    }

    [Fact]
    public async Task Settings_RoundTrip_AndThePasswordIsNotSaved()
    {
        await using var page = CreatePage();
        page.Host = "broker.example";
        page.Port = 8883;
        page.ClientId = "device-1";
        page.UserName = "operator";
        page.Password = "secret";
        page.ProtocolVersion = MqttProtocolVersion.V500;
        page.SessionExpiryInterval = 120;
        page.UseTls = true;
        page.AllowUntrustedCertificates = true;
        page.WillTopic = "devices/1/status";
        page.WillPayload = "offline";
        page.WillQos = MqttQos.ExactlyOnce;
        page.WillRetain = true;

        var saved = (MqttSettings)page.CaptureSettings();
        await using var other = CreatePage();
        other.ApplySettings(saved);

        Assert.Equal(saved, other.CaptureSettings());
        Assert.Equal(string.Empty, other.Password);
        Assert.True(other.IsVersion500);
    }

    [Fact]
    public async Task ConnectWithoutAHost_IsRefusedWithInvalidRequest_AndRecorded()
    {
        await using var page = CreatePage();
        page.Host = string.Empty;

        await page.ConnectCommand.ExecuteAsync(null);

        Assert.Equal(SessionState.NotConnected, page.State);
        OperationRecord record = Assert.Single(page.Operations);
        Assert.Equal(("连接", false, CommErrorKind.InvalidRequest), (record.Operation, record.Succeeded, record.ErrorKind));
        Assert.Equal("主机地址不能为空。", record.Message);
    }

    [Fact]
    public async Task SubscribeAndPublish_AreUnavailable_UntilConnected()
    {
        await using var page = CreatePage();

        Assert.Equal((true, false, false, false), (page.ConnectCommand.CanExecute(null), page.SubscribeCommand.CanExecute(null), page.PublishCommand.CanExecute(null), page.IsLinked));
    }

    [Fact]
    public async Task UserProperties_AddAndRemove_AndRejectAnEmptyName()
    {
        await using var page = CreatePage();

        page.NewUserPropertyName = "  ";
        page.AddUserPropertyCommand.Execute(null);
        Assert.Equal("用户属性的名称不能为空。", page.PublishMessage);
        Assert.Empty(page.PublishUserProperties);

        page.NewUserPropertyName = " tenant ";
        page.NewUserPropertyValue = "acme";
        page.AddUserPropertyCommand.Execute(null);
        Assert.Equal(new MqttUserPropertyRow("tenant", "acme"), Assert.Single(page.PublishUserProperties));
        Assert.Equal((string.Empty, string.Empty), (page.NewUserPropertyName, page.NewUserPropertyValue));

        page.RemoveUserPropertyCommand.Execute(page.PublishUserProperties[0]);
        Assert.Empty(page.PublishUserProperties);
    }

    [Fact]
    public async Task VersionSwitch_ChangesWhichVersionDependentOptionsApply()
    {
        await using var page = CreatePage();

        page.ProtocolVersion = MqttProtocolVersion.V500;

        Assert.Equal((false, true), (page.IsVersion311, page.IsVersion500));
    }
}
