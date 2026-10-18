namespace Junevy.Communication.Ftp.Tests;

/// <summary>
/// 选项的默认值与校验规则（计划 Task 2 第 2 条；P63-D5、D12、D13、D14）。
/// </summary>
public sealed class FtpClientOptionsTests
{
    public static TheoryData<string, Action<FtpClientOptions>> InvalidOptions => new()
    {
        { "Host", o => o.Host = null },
        { "Host", o => o.Host = string.Empty },
        { "Port", o => o.Port = 0 },
        { "Port", o => o.Port = 65536 },
        { "ConnectTimeout", o => o.ConnectTimeout = 0 },
        { "ReadTimeout", o => o.ReadTimeout = -1 },
        { "DataConnectionTimeout", o => o.DataConnectionTimeout = 0 },
        { "Encryption", o => o.Encryption = (FtpEncryption)99 },
        { "EncodingName", o => o.EncodingName = string.Empty },
        { "EncodingName", o => o.EncodingName = "no-such-encoding" },
        { "UserName", o => o.Password = "secret" },
    };

    [Fact]
    public void Defaults_MatchTheDesign()
    {
        var options = new FtpClientOptions();

        Assert.Null(options.Host);
        Assert.Equal(21, options.Port);
        Assert.Null(options.UserName);
        Assert.Null(options.Password);
        Assert.Equal(FtpEncryption.None, options.Encryption);
        Assert.False(options.AllowUntrustedCertificates);
        Assert.Null(options.EncodingName);
        Assert.Equal(10000, options.ConnectTimeout);
        Assert.Equal(15000, options.ReadTimeout);
        Assert.Equal(15000, options.DataConnectionTimeout);
    }

    [Fact]
    public void Validate_AcceptsAnAnonymousHost()
    {
        FtpClientOptionsValidator.Validate(new FtpClientOptions { Host = "ftp.local" });
    }

    [Fact]
    public void Validate_AcceptsCredentialsGbkAndImplicitTls()
    {
        var options = new FtpClientOptions
        {
            Host = "ftp.local",
            Port = 990,
            UserName = "line1",
            Password = "secret",
            Encryption = FtpEncryption.Implicit,
            AllowUntrustedCertificates = true,
            EncodingName = "GBK",
        };

        FtpClientOptionsValidator.Validate(options);
    }

    [Fact]
    public void Validate_RejectsNull()
    {
        Assert.Throws<ArgumentNullException>(() => FtpClientOptionsValidator.Validate(null!));
    }

    [Theory]
    [MemberData(nameof(InvalidOptions))]
    public void Validate_RejectsAnInvalidValue_NamingTheProperty(string property, Action<FtpClientOptions> mutate)
    {
        var options = new FtpClientOptions { Host = "ftp.local" };
        mutate(options);

        ArgumentException ex = Assert.ThrowsAny<ArgumentException>(() => FtpClientOptionsValidator.Validate(options));
        Assert.Contains(property, ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Validate_PortOutOfRange_IsAnArgumentOutOfRangeNamingTheParameter()
    {
        var options = new FtpClientOptions { Host = "ftp.local", Port = 0 };

        var ex = Assert.Throws<ArgumentOutOfRangeException>(() => FtpClientOptionsValidator.Validate(options));
        Assert.Equal("options.Port", ex.ParamName);
    }
}
