using Junevy.Communication.Core.Results;
using Junevy.Communication.DebugAssistant.Protocols.Secs;
using Junevy.Communication.DebugAssistant.Sessions;
using Junevy.Communication.DebugAssistant.Shell;
using Junevy.Communication.DebugAssistant.Tests.Support;
using Junevy.Communication.DebugAssistant.Workspace;
using Junevy.Communication.Secs;
using Junevy.Communication.Secs.Hsms;

namespace Junevy.Communication.DebugAssistant.Tests.Protocols.Secs;

/// <summary>
/// SECS/HSMS 设备端页的视图模型（计划 Task 15 第 1 条）：参数不合法时拒绝启动并给出原因，设置往返，自动应答规则按 S/F 登记与替换、
/// 不合法时给出行号与列号，模板填入发送区，未运行时发送。网络行为见 <c>SecsHsmsEndToEndTests</c>。
/// </summary>
public sealed class SecsSessionViewModelTests
{
    private const int DefaultMaxMessageLength = 16 * 1024 * 1024;

    private static SecsSessionViewModel CreatePage() =>
        new(ProtocolCatalog.Get(ProtocolKind.Secs), FakeServices.Create(new ManualUiDispatcher()));

    [Fact]
    public async Task Page_StartsStoppedWithNoSession()
    {
        await using var page = CreatePage();

        Assert.Equal(("未连接", SessionState.NotConnected, true), (page.HsmsStateText, page.State, page.IsEditable));
    }

    [Fact]
    public async Task Settings_RoundTripThroughTheSavedRecord()
    {
        await using var page = CreatePage();
        page.Mode = HsmsConnectionMode.Active;
        page.Host = "192.168.1.10";
        page.Port = 5001;
        page.DeviceId = 3;
        page.T3 = 1234;
        SetRule(page, 6, 11, SecsRuleKind.Reply, "<U4 1>");

        var saved = (SecsSettings)page.CaptureSettings();
        await using var other = CreatePage();
        other.ApplySettings(saved);
        var again = (SecsSettings)other.CaptureSettings();

        Assert.Equal((saved.Mode, saved.Host, saved.Port, saved.DeviceId, saved.T3), (again.Mode, again.Host, again.Port, again.DeviceId, again.T3));
        Assert.Equal(saved.Rules, again.Rules);
        Assert.Equal(HsmsConnectionMode.Active, other.Mode);
    }

    [Fact]
    public async Task InvalidSettings_RefuseToStart_AndSayWhy()
    {
        Assert.Equal("主动模式必须填写主机地址（IP 或主机名）。", await RefusalOf(page => page.Mode = HsmsConnectionMode.Active));
        Assert.Equal("被动模式的监听地址必须是 IP 地址；留空表示所有地址。", await RefusalOf(page => page.Host = "not-an-ip"));
        Assert.Equal("端口必须在 1 到 65535 之间。", await RefusalOf(page => page.Port = 0));
        Assert.Equal("设备号必须在 0 到 32767 之间。", await RefusalOf(page => page.DeviceId = 40000));
        Assert.Equal("T3 必须是正数（毫秒）。", await RefusalOf(page => page.T3 = 0));
        Assert.Equal("最大报文长度不能小于 1024 字节。", await RefusalOf(page => page.MaxMessageLength = 100));
    }

    [Fact]
    public async Task Rules_AreKeyedByStreamAndFunction_AndReplaced()
    {
        await using var page = CreatePage();

        SetRule(page, 1, 1, SecsRuleKind.Reply, "<A \"EQ01\">");
        Assert.Equal(("已登记 S1F1。", 1), (page.RuleMessage, page.Rules.Count));

        SetRule(page, 1, 1, SecsRuleKind.Abort, string.Empty);
        Assert.Equal((1, SecsRuleKind.Abort, "中止（SxF0）"), (page.Rules.Count, page.Rules[0].Kind, page.Rules[0].KindText));
    }

    [Fact]
    public async Task Rules_RefuseEvenFunctionsAndBadSml_WithALineAndColumn()
    {
        await using var page = CreatePage();

        SetRule(page, 1, 2, SecsRuleKind.Reply, "<U1 1>");
        Assert.Equal("F 必须是 1 到 255 之间的奇数（主消息的功能号）。", page.RuleMessage);

        SetRule(page, 2, 13, SecsRuleKind.Reply, "<U1 300>");
        Assert.Equal("第 1 行第 5 列：U1 的值 300 超出 0 到 255", page.RuleMessage);

        // 中止规则不使用正文，因此正文即使写错也可以登记。
        SetRule(page, 2, 13, SecsRuleKind.Abort, "<U1 300>");
        Assert.Equal((2, 13), (Assert.Single(page.Rules).Stream, page.Rules[0].Function));
    }

    [Fact]
    public async Task RemoveRule_DeletesTheChosenRule()
    {
        await using var page = CreatePage();
        SetRule(page, 1, 1, SecsRuleKind.Abort, string.Empty);
        SetRule(page, 5, 1, SecsRuleKind.Abort, string.Empty);

        page.RemoveRuleCommand.Execute(page.Rules[0]);

        Assert.Equal("已删除 S1F1。", page.RuleMessage);
        Assert.Equal(new[] { "S5F1" }, page.Rules.Select(rule => $"S{rule.Stream}F{rule.Function}").ToArray());
    }

    [Fact]
    public async Task ApplySettings_RefusesAStoredRuleWithBadSml()
    {
        await using var page = CreatePage();
        var stored = new SecsSettings(
            HsmsConnectionMode.Passive,
            null,
            5000,
            0,
            45000,
            10000,
            5000,
            10000,
            5000,
            0,
            DefaultMaxMessageLength,
            [new SecsRule(1, 1, SecsRuleKind.Reply, "<U1 300>")]);

        ArgumentException error = Assert.Throws<ArgumentException>(() => page.ApplySettings(stored));

        Assert.Empty(page.Rules);
        Assert.Contains("S1F1", error.Message);
    }

    [Fact]
    public async Task Templates_FillTheSendArea_AndTheirBodiesParse()
    {
        await using var page = CreatePage();
        Assert.Equal(5, page.Templates.Count);
        foreach (SecsTemplate template in page.Templates)
        {
            Assert.Null(SecsSupport.SendProblem(template.Stream, template.Function, template.Sml, out _));
        }

        page.SelectedTemplate = page.Templates[1];
        page.ApplyTemplateCommand.Execute(null);

        Assert.Equal((1, 13, true), (page.SendStream, page.SendFunction, page.SendReplyExpected));
        Assert.Contains("EQ01", page.SendSml);
    }

    [Fact]
    public async Task Send_WhenStopped_SaysSoAndSendsNothing()
    {
        await using var page = CreatePage();

        await page.SendCommand.ExecuteAsync(null);

        Assert.Equal("页签没有运行，请先启动。", page.SendMessage);
        Assert.Empty(page.Operations);
    }

    [Fact]
    public void SendArea_RefusesEvenFunctionsAndBadBodies()
    {
        Assert.Equal("F 必须是 1 到 255 之间的奇数；发送的必须是主消息。", SecsSupport.SendProblem(5, 2, string.Empty, out _));
        Assert.Equal("第 1 行第 1 列：数据项应以 '<' 开始", SecsSupport.SendProblem(5, 1, "U1 1", out _));
        Assert.Null(SecsSupport.SendProblem(5, 1, string.Empty, out SecsItem? empty));
        Assert.Null(empty);
    }

    [Fact]
    public void StateNames_AreTheSessionStatesOfThePage()
    {
        string[] expected = ["未连接", "未选择", "已选择"];
        Assert.Equal(expected, new[] { HsmsState.NotConnected, HsmsState.NotSelected, HsmsState.Selected }.Select(SecsSupport.StateName).ToArray());
    }

    private static void SetRule(SecsSessionViewModel page, int stream, int function, SecsRuleKind kind, string sml)
    {
        page.NewRuleStream = stream;
        page.NewRuleFunction = function;
        page.NewRuleKind = kind;
        page.NewRuleSml = sml;
        page.AddRuleCommand.Execute(null);
    }

    private static async Task<string> RefusalOf(Action<SecsSessionViewModel> change)
    {
        await using var page = CreatePage();
        change(page);

        await page.ConnectCommand.ExecuteAsync(null);

        Assert.Equal(SessionState.NotConnected, page.State);
        OperationRecord record = Assert.Single(page.Operations);
        Assert.Equal(CommErrorKind.InvalidRequest, record.ErrorKind);
        return record.Message;
    }
}
