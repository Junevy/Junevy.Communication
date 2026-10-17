using Junevy.Communication.OpcUa;
using Xunit;

namespace Junevy.Communication.OpcUa.Tests;

/// <summary>
/// 选项校验的每一条规则（计划第 6 节第 1 条、每条都有用例）。
/// </summary>
public sealed class OpcUaClientOptionsTests
{
    private static OpcUaClientOptions Valid() => new OpcUaClientOptions
    {
        EndpointUrl = "opc.tcp://127.0.0.1:4840/",
    };

    [Fact]
    public void Create_NothingAtAll_Throws()
    {
        var error = Assert.Throws<ArgumentException>(() => OpcUaOptionsValidator.Validate(new OpcUaClientOptions()));

        Assert.Equal("options.EndpointUrl", error.ParamName);
    }

    [Fact]
    public void Create_NullOptions_Throws()
        => Assert.Throws<ArgumentNullException>(() => OpcUaOptionsValidator.Validate((OpcUaClientOptions)null!));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Create_BlankEndpointUrl_Throws(string? url)
    {
        OpcUaClientOptions options = Valid();
        options.EndpointUrl = url;

        Assert.Equal("options.EndpointUrl", Assert.Throws<ArgumentException>(() => OpcUaOptionsValidator.Validate(options)).ParamName);
    }

    [Theory]
    [InlineData("http://127.0.0.1:4840/")]
    [InlineData("opc.tcp:/127.0.0.1:4840")]
    [InlineData("/UA/")]
    [InlineData("opc.tcp:")]
    public void Create_EndpointUrlIsNotAnAbsoluteOpcTcpUri_Throws(string url)
    {
        OpcUaClientOptions options = Valid();
        options.EndpointUrl = url;

        Assert.Equal("options.EndpointUrl", Assert.Throws<ArgumentException>(() => OpcUaOptionsValidator.Validate(options)).ParamName);
    }

    [Theory]
    [InlineData("opc.tcp://127.0.0.1:4840/")]
    [InlineData("opc.tcp://machine.example.com:4840")]
    [InlineData("OPC.TCP://host/UA/")]
    public void Create_AbsoluteOpcTcpUri_Passes(string url)
    {
        OpcUaClientOptions options = Valid();
        options.EndpointUrl = url;

        OpcUaOptionsValidator.Validate(options);
    }

    [Fact]
    public void Create_EmptyApplicationName_Throws()
    {
        OpcUaClientOptions options = Valid();
        options.ApplicationName = "  ";

        Assert.Equal("options.ApplicationName", Assert.Throws<ArgumentException>(() => OpcUaOptionsValidator.Validate(options)).ParamName);
    }

    [Fact]
    public void Create_BlankApplicationUri_Throws()
    {
        OpcUaClientOptions options = Valid();
        options.ApplicationUri = "";

        Assert.Equal("options.ApplicationUri", Assert.Throws<ArgumentException>(() => OpcUaOptionsValidator.Validate(options)).ParamName);
    }

    [Fact]
    public void Create_NullApplicationUri_Passes()
    {
        OpcUaClientOptions options = Valid();
        options.ApplicationUri = null;

        OpcUaOptionsValidator.Validate(options);
    }

    [Fact]
    public void Create_BlankPkiRoot_Throws()
    {
        OpcUaClientOptions options = Valid();
        options.PkiRoot = "";

        Assert.Equal("options.PkiRoot", Assert.Throws<ArgumentException>(() => OpcUaOptionsValidator.Validate(options)).ParamName);
    }

    [Theory]
    [InlineData(nameof(OpcUaClientOptions.ConnectTimeout))]
    [InlineData(nameof(OpcUaClientOptions.OperationTimeout))]
    [InlineData(nameof(OpcUaClientOptions.SessionTimeout))]
    [InlineData(nameof(OpcUaClientOptions.KeepAliveInterval))]
    [InlineData(nameof(OpcUaClientOptions.ReconnectInterval))]
    [InlineData(nameof(OpcUaClientOptions.MaxReconnectInterval))]
    public void Create_NonPositiveInterval_Throws(string property)
    {
        OpcUaClientOptions options = Valid();
        typeof(OpcUaClientOptions).GetProperty(property)!.SetValue(options, 0);
        Assert.Equal("options." + property, Assert.Throws<ArgumentException>(() => OpcUaOptionsValidator.Validate(options)).ParamName);

        typeof(OpcUaClientOptions).GetProperty(property)!.SetValue(options, -1);
        Assert.Equal("options." + property, Assert.Throws<ArgumentException>(() => OpcUaOptionsValidator.Validate(options)).ParamName);
    }

    [Fact]
    public void Create_MaxReconnectIntervalBelowTheInitialOne_Throws()
    {
        OpcUaClientOptions options = Valid();
        options.ReconnectInterval = 5000;
        options.MaxReconnectInterval = 4999;

        Assert.Equal("options.MaxReconnectInterval", Assert.Throws<ArgumentException>(() => OpcUaOptionsValidator.Validate(options)).ParamName);
    }

    [Fact]
    public void Create_MaxReconnectIntervalEqualToTheInitialOne_Passes()
    {
        OpcUaClientOptions options = Valid();
        options.ReconnectInterval = 5000;
        options.MaxReconnectInterval = 5000;

        OpcUaOptionsValidator.Validate(options);
    }

    [Fact]
    public void Defaults_AreTheDocumentedOnes()
    {
        OpcUaClientOptions options = new OpcUaClientOptions();

        Assert.Equal("Junevy.Communication.OpcUa", options.ApplicationName);
        Assert.Null(options.ApplicationUri);
        Assert.Equal(10000, options.ConnectTimeout);
        Assert.Equal(10000, options.OperationTimeout);
        Assert.Equal(60000, options.SessionTimeout);
        Assert.Equal(5000, options.KeepAliveInterval);
        Assert.Equal(1000, options.ReconnectInterval);
        Assert.Equal(30000, options.MaxReconnectInterval);
    }

    [Fact]
    public void DefaultPkiRoot_LivesUnderLocalApplicationData()
    {
        string root = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Junevy.Communication",
            "OpcUa",
            "pki");

        Assert.Equal(root, OpcUaClientOptions.DefaultPkiRoot);
        Assert.Equal(OpcUaClientOptions.DefaultPkiRoot, new OpcUaClientOptions().PkiRoot);
    }

    [Theory]
    [InlineData(nameof(OpcUaSubscriptionOptions.PublishingInterval), 0)]
    [InlineData(nameof(OpcUaSubscriptionOptions.PublishingInterval), -1)]
    public void Subscription_NonPositivePublishingInterval_Throws(string property, int value)
    {
        OpcUaSubscriptionOptions options = new OpcUaSubscriptionOptions();
        typeof(OpcUaSubscriptionOptions).GetProperty(property)!.SetValue(options, value);

        Assert.Equal("options." + property, Assert.Throws<ArgumentException>(() => OpcUaOptionsValidator.Validate(options)).ParamName);
    }

    [Fact]
    public void Subscription_NegativeSamplingInterval_Throws()
    {
        OpcUaSubscriptionOptions options = new OpcUaSubscriptionOptions { SamplingInterval = -1 };

        Assert.Equal("options.SamplingInterval", Assert.Throws<ArgumentException>(() => OpcUaOptionsValidator.Validate(options)).ParamName);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(10)]
    public void Subscription_SamplingIntervalNullOrNonNegative_Passes(int? samplingInterval)
    {
        OpcUaSubscriptionOptions options = new OpcUaSubscriptionOptions { SamplingInterval = samplingInterval };

        OpcUaOptionsValidator.Validate(options);
    }

    [Fact]
    public void Subscription_ZeroQueueSize_Throws()
    {
        OpcUaSubscriptionOptions options = new OpcUaSubscriptionOptions { QueueSize = 0 };

        Assert.Equal("options.QueueSize", Assert.Throws<ArgumentException>(() => OpcUaOptionsValidator.Validate(options)).ParamName);
    }

    [Fact]
    public void Subscription_NullOptions_Throws()
        => Assert.Throws<ArgumentNullException>(() => OpcUaOptionsValidator.Validate((OpcUaSubscriptionOptions)null!));

    [Fact]
    public void Subscription_Defaults_AreTheDocumentedOnes()
    {
        OpcUaSubscriptionOptions options = new OpcUaSubscriptionOptions();

        Assert.Equal(1000, options.PublishingInterval);
        Assert.Null(options.SamplingInterval);
        Assert.Equal(1u, options.QueueSize);
        Assert.True(options.DiscardOldest);
        OpcUaOptionsValidator.Validate(options);
    }
}
