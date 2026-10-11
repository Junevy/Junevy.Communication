using Junevy.Communication.WebApi.Soap;

namespace Junevy.Communication.WebApi.Tests;

/// <summary>
/// SOAP 配置校验与快照（<see cref="SoapConfigRules"/>，P3-D16）：Url、Version、DefaultNamespace、HealthCheckUrl；共有字段由 <c>EndpointConfigRules</c> 把关。
/// </summary>
public sealed class SoapConfigRulesTests
{
    public static IEnumerable<object[]> InvalidCases()
    {
        yield return Case("Url", c => c.Url = "");
        yield return Case("Url", c => c.Url = "  ");
        yield return Case("Url", c => c.Url = null!);
        yield return Case("Url", c => c.Url = "mes.local/service.asmx");
        yield return Case("Url", c => c.Url = "/service.asmx");
        yield return Case("Url", c => c.Url = "ftp://mes.local/service.asmx");
        yield return Case("Url", c => c.Url = "http://mes.local/service.asmx#frag");
        yield return Case("Version", c => c.Version = (SoapVersion)7);
        yield return Case("DefaultNamespace", c => c.DefaultNamespace = "   ");
        yield return Case("HealthCheckUrl", c => c.HealthCheckUrl = "service.asmx?wsdl");
        yield return Case("HealthCheckUrl", c => c.HealthCheckUrl = "ftp://mes.local/service.asmx?wsdl");
        yield return Case("HealthCheckUrl", c => c.HealthCheckUrl = "http://other.local/service.asmx?wsdl");
        yield return Case("HealthCheckUrl", c => c.HealthCheckUrl = "https://mes.local/service.asmx?wsdl");
        yield return Case("HealthCheckUrl", c => c.HealthCheckUrl = "http://mes.local:8080/service.asmx?wsdl");
        yield return Case("Timeout", c => c.Timeout = 0);
        yield return Case("MaxConnectionsPerServer", c => c.MaxConnectionsPerServer = 0);
    }

    [Theory]
    [MemberData(nameof(InvalidCases))]
    public void Create_InvalidField_ThrowsArgumentExceptionNamingTheField(string field, Action<SoapClientConfig> mutate)
    {
        SoapClientConfig config = Valid();
        mutate(config);

        var exception = Assert.ThrowsAny<ArgumentException>(() => SoapConfigRules.Create(config));

        Assert.Equal(field, exception.ParamName);
        Assert.Contains(field, exception.Message);
    }

    [Fact]
    public void Create_NullConfig_Throws()
    {
        var exception = Assert.Throws<ArgumentNullException>(() => SoapConfigRules.Create(null!));

        Assert.Equal("config", exception.ParamName);
    }

    [Fact]
    public void Create_CopiesTheValidatedValues()
    {
        SoapClientConfig config = Valid();
        config.Version = SoapVersion.Soap12;
        config.DefaultNamespace = "http://mes.local/ns";
        config.HealthCheckUrl = "http://mes.local/service.asmx?wsdl";
        config.Timeout = 2345;

        SoapSettings settings = SoapConfigRules.Create(config);

        Assert.Equal("http://mes.local/service.asmx", settings.Address.AbsoluteUri);
        Assert.Equal(SoapVersion.Soap12, settings.Version);
        Assert.Equal("http://mes.local/ns", settings.DefaultNamespace);
        Assert.Equal("http://mes.local/service.asmx?wsdl", settings.HealthCheckUri!.AbsoluteUri);
        Assert.Equal(2345, settings.Endpoint.Timeout);
    }

    [Fact]
    public void Create_TheUrlMayCarryAQuery_AndTheDefaultsHaveNoNamespaceOrHealthCheck()
    {
        SoapClientConfig config = Valid();
        config.Url = "  https://mes.local:8443/svc?token=abc  ";

        SoapSettings settings = SoapConfigRules.Create(config);

        Assert.Equal("https://mes.local:8443/svc?token=abc", settings.Address.AbsoluteUri);
        Assert.Null(settings.DefaultNamespace);
        Assert.Null(settings.HealthCheckUri);
        Assert.Equal(SoapVersion.Soap11, settings.Version);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  ")]
    public void Create_WithoutAHealthCheckUrl_HasNone(string? url)
    {
        SoapClientConfig config = Valid();
        config.HealthCheckUrl = url;

        Assert.Null(SoapConfigRules.Create(config).HealthCheckUri);
    }

    [Fact]
    public void Create_SnapshotIsIsolatedFromLaterChanges()
    {
        SoapClientConfig config = Valid();
        config.DefaultNamespace = "urn:a";
        SoapSettings settings = SoapConfigRules.Create(config);

        config.Url = "http://elsewhere.local/";
        config.DefaultNamespace = "urn:b";
        config.Version = SoapVersion.Soap12;

        Assert.Equal("http://mes.local/service.asmx", settings.Address.AbsoluteUri);
        Assert.Equal("urn:a", settings.DefaultNamespace);
        Assert.Equal(SoapVersion.Soap11, settings.Version);
    }

    [Fact]
    public void SoapVersion_NumericValuesAreStable()
    {
        Assert.Equal(0, (int)SoapVersion.Soap11);
        Assert.Equal(1, (int)SoapVersion.Soap12);
    }

    private static SoapClientConfig Valid() => new SoapClientConfig { Url = "http://mes.local/service.asmx" };

    private static object[] Case(string field, Action<SoapClientConfig> mutate) => new object[] { field, mutate };
}
