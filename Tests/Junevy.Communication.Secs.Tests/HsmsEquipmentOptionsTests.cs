using Junevy.Communication.Secs.Hsms;
using Junevy.Communication.Secs.Protocol;
using Junevy.Communication.Testing;

namespace Junevy.Communication.Secs.Tests;

/// <summary>
/// 选项校验与快照（计划 Task 8，P51-D9、D17）：每条校验一条用例，非法值的消息写明属性名与范围；
/// <see cref="HsmsEquipment.Create"/> 取快照之后，调用方再改选项对象不影响已建好的设备。
/// </summary>
[Collection(SocketTimingCollection.Name)]
public sealed class HsmsEquipmentOptionsTests
{
    private const ushort DeviceId = HsmsEquipmentRig.DefaultDeviceId;

    [Fact]
    public void Defaults_AreAValidPassiveEquipment()
    {
        var options = new HsmsEquipmentOptions();

        // 默认值：Passive、监听所有地址、端口 5000（SEMI E37 的标准端口）、设备号 0。
        Assert.Equal(HsmsConnectionMode.Passive, options.Mode);
        Assert.Equal(5000, options.Port);

        using HsmsEquipment equipment = HsmsEquipment.Create(options);
        Assert.Equal(HsmsState.NotConnected, equipment.State);
    }

    [Fact]
    public void Create_NullOptions_ThrowsArgumentNullException()
    {
        Assert.Throws<ArgumentNullException>(() => HsmsEquipment.Create(null!));
    }

    [Fact]
    public void AnUnknownMode_IsRejected()
    {
        ArgumentException error = AssertRejected(options => options.Mode = (HsmsConnectionMode)7, "Mode");

        Assert.Contains("Passive or Active", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void TheActiveMode_RequiresTheHostAddress()
    {
        // 主动端必须说清连哪台主机：null 与纯空白都不行。
        AssertRejected(
            options =>
            {
                options.Mode = HsmsConnectionMode.Active;
                options.Host = null;
            },
            "Host");
        AssertRejected(
            options =>
            {
                options.Mode = HsmsConnectionMode.Active;
                options.Host = "  ";
            },
            "Host");

        // 主动端给了地址就合法。
        using var equipment = HsmsEquipment.Create(new HsmsEquipmentOptions
        {
            Mode = HsmsConnectionMode.Active,
            Host = LoopbackTcp.Host,
            Port = HsmsPorts.Free(),
        });
        Assert.Equal(HsmsState.NotConnected, equipment.State);
    }

    [Fact]
    public void APassiveListenAddressThatIsNotAnIpAddress_IsRejected()
    {
        // 被动端的 Host 是本机的监听地址，只能是 IP（或 null 表示所有地址）。
        AssertRejected(options => options.Host = "host.example.com", "Host");
    }

    [Fact]
    public void AnActiveHost_MayBeAHostName()
    {
        // 主动端的 Host 是对端地址，与其他 TCP 客户端一样由通道解析主机名（连接用例见 HsmsEquipmentActiveTests）。
        HsmsEquipmentOptions options = HsmsEquipmentRig.FastOptions();
        options.Mode = HsmsConnectionMode.Active;
        options.Host = "host.example.com";

        using HsmsEquipment equipment = HsmsEquipment.Create(options);

        Assert.Equal(HsmsState.NotConnected, equipment.State);
    }

    [Theory]
    [InlineData(0, "1", "65535")]
    [InlineData(-1, "1", "65535")]
    [InlineData(65536, "1", "65535")]
    public void APortOutsideTheRange_IsRejected(int port, string minimum, string maximum)
    {
        ArgumentException error = AssertRejected(options => options.Port = port, "Port");

        Assert.Contains($"[{minimum}, {maximum}]", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AFailedSelect_IsRejected()
    {
        // 32768 是 ushort 里合法的值，但超出设备号上限；不写 unchecked 的话编译器会把它折成 0。
        ArgumentException error = AssertRejected(options => options.DeviceId = unchecked((ushort)32768), "DeviceId");

        Assert.Contains("[0, 32767]", error.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("T3")]
    [InlineData("T5")]
    [InlineData("T6")]
    [InlineData("T7")]
    [InlineData("T8")]
    public void ATimerThatIsNotPositive_IsRejected(string timer)
    {
        AssertRejected(options => SetTimer(options, timer, 0), timer);

        // 每条计时器各有一次自己归零，不会被别的校验截住。
        AssertRejected(options => SetTimer(options, timer, -1), timer);
    }

    [Fact]
    public void ANegativeLinktestInterval_IsRejected()
    {
        // 0 是合法的：表示不发 Linktest（测试选项的缺省就是它）。
        Assert.Equal(0, HsmsEquipmentRig.FastOptions().LinktestInterval);

        ArgumentException error = AssertRejected(options => options.LinktestInterval = -1, "LinktestInterval");

        Assert.Contains("[0, ", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AMessageLengthBelowTheLowerBound_IsRejected()
    {
        ArgumentException error = AssertRejected(options => options.MaxMessageLength = 1023, "MaxMessageLength");

        Assert.Contains("[1024, ", error.Message, StringComparison.Ordinal);
    }

    [Fact(Timeout = 30000)]
    public async Task TheOptionsAreSnapshotted_SoLaterChangesDoNotAffectTheEquipment()
    {
        HsmsEquipmentOptions options = HsmsEquipmentRig.FastOptions();
        int port = options.Port;
        await using HsmsEquipment equipment = HsmsEquipment.Create(options);

        // 建好之后把选项改得面目全非：监听端口、设备号与 T3 都不该再被读到。
        options.Port = HsmsPorts.Free();
        options.DeviceId = 7;
        options.T3 = 120000;

        Assert.True((await equipment.StartAsync()).IsSuccess);
        await using HsmsHostConnector host = await HsmsHostConnector.ConnectAsync(port);

        // 设备端按建好时的端口监听、按建好时的设备号应答会话号，因此 Select 成功。
        await host.SendAsync(HsmsControl.SelectRequest(1).ToArray());
        HsmsHostFrame answer = await host.NextFrameAsync(5000);
        Assert.Equal(HsmsControl.SelectOk, answer.Message.Status);
        Assert.Equal(HsmsState.Selected, equipment.State);
    }

    // 断言某项改动被 Create 拒绝，并返回异常给调用方检查消息。
    private static ArgumentException AssertRejected(Action<HsmsEquipmentOptions> mutate, string property)
    {
        HsmsEquipmentOptions options = HsmsEquipmentRig.FastOptions();
        mutate(options);

        ArgumentException error = Assert.Throws<ArgumentException>(() => HsmsEquipment.Create(options));
        Assert.Contains(property, error.Message, StringComparison.Ordinal);
        return error;
    }

    private static void SetTimer(HsmsEquipmentOptions options, string timer, int value)
    {
        switch (timer)
        {
            case "T3":
                options.T3 = value;
                break;
            case "T5":
                options.T5 = value;
                break;
            case "T6":
                options.T6 = value;
                break;
            case "T7":
                options.T7 = value;
                break;
            default:
                options.T8 = value;
                break;
        }
    }
}