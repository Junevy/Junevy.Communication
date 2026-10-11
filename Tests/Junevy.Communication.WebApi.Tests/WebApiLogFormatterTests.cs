using System.Text;
using Junevy.Communication.Core.Results;
using Junevy.Communication.Testing;
using Junevy.Communication.WebApi.Http;
using Microsoft.Extensions.Logging;

namespace Junevy.Communication.WebApi.Tests;

/// <summary>
/// <see cref="WebApiLogFormatter"/> 的测试：每次尝试的 Debug 日志、正文的记录与截断（P3-D17）。
/// </summary>
public sealed class WebApiLogFormatterTests
{
    private static readonly Uri RedactedUri = new Uri("http://mes.local/api?token=***");

    [Fact]
    public void LogRequest_WritesMethodAndUriAtDebugLevelWithoutBodies()
    {
        var logger = new TestLogger();
        WebApiLogFormatter formatter = Create(logger);

        formatter.LogRequest(Sending(Encoding.UTF8.GetBytes("secret body")));

        TestLogEntry entry = Assert.Single(logger.Entries);
        Assert.Equal(LogLevel.Debug, entry.Level);
        Assert.Contains("POST", entry.Message);
        Assert.Contains("token=***", entry.Message);
        Assert.Contains("mes", entry.Message);
        Assert.DoesNotContain("secret body", entry.Message);
    }

    [Fact]
    public void LogRequest_WithLogBodies_AddsTheBodyEntry()
    {
        var logger = new TestLogger();
        WebApiLogFormatter formatter = Create(logger, logBodies: true);

        formatter.LogRequest(Sending(Encoding.UTF8.GetBytes("{\"order\":\"工单\"}")));

        Assert.Equal(2, logger.Entries.Count);
        Assert.Contains("{\"order\":\"工单\"}", logger.Entries[1].Message);
    }

    [Fact]
    public void LogRequest_WithLogBodiesAndEmptyBody_AddsNoBodyEntry()
    {
        var logger = new TestLogger();
        WebApiLogFormatter formatter = Create(logger, logBodies: true);

        formatter.LogRequest(Sending(Array.Empty<byte>()));

        Assert.Single(logger.Entries);
    }

    [Fact]
    public void LogResponse_WritesStatusAndElapsed()
    {
        var logger = new TestLogger();
        WebApiLogFormatter formatter = Create(logger);

        formatter.LogResponse(Response(503, CommErrorKind.RemoteError, Encoding.UTF8.GetBytes("busy"), TimeSpan.FromMilliseconds(42)));

        TestLogEntry entry = Assert.Single(logger.Entries);
        Assert.Equal(LogLevel.Debug, entry.Level);
        Assert.Contains("503", entry.Message);
        Assert.Contains("42", entry.Message);
        Assert.DoesNotContain("busy", entry.Message);
    }

    [Fact]
    public void LogResponse_NetworkFailure_ReportsNoResponseAndTheKind()
    {
        var logger = new TestLogger();
        WebApiLogFormatter formatter = Create(logger);

        formatter.LogResponse(Response(null, CommErrorKind.ConnectionClosed, Array.Empty<byte>(), TimeSpan.FromMilliseconds(5)));

        TestLogEntry entry = Assert.Single(logger.Entries);
        Assert.Contains("no response", entry.Message);
        Assert.Contains("ConnectionClosed", entry.Message);
    }

    [Fact]
    public void LogResponse_WithLogBodies_AddsTheBodyEntry()
    {
        var logger = new TestLogger();
        WebApiLogFormatter formatter = Create(logger, logBodies: true);

        formatter.LogResponse(Response(200, CommErrorKind.None, Encoding.UTF8.GetBytes("{\"code\":0}"), TimeSpan.Zero));

        Assert.Equal(2, logger.Entries.Count);
        Assert.Contains("{\"code\":0}", logger.Entries[1].Message);
    }

    [Fact]
    public void LogBody_LongerThanTheLimit_IsTruncatedAndStatesTheOriginalLength()
    {
        var logger = new TestLogger();
        WebApiLogFormatter formatter = Create(logger, logBodies: true, maxLength: 5);

        formatter.LogRequest(Sending(Encoding.UTF8.GetBytes("0123456789")));

        string message = logger.Entries[1].Message;
        Assert.Contains("01234", message);
        Assert.DoesNotContain("012345", message);
        Assert.Contains("truncated", message);
        Assert.Contains("10", message);
    }

    [Fact]
    public void LogBody_ExactlyAtTheLimit_IsNotTruncated()
    {
        var logger = new TestLogger();
        WebApiLogFormatter formatter = Create(logger, logBodies: true, maxLength: 10);

        formatter.LogRequest(Sending(Encoding.UTF8.GetBytes("0123456789")));

        Assert.Contains("0123456789", logger.Entries[1].Message);
        Assert.DoesNotContain("truncated", logger.Entries[1].Message);
    }

    [Fact]
    public void Log_WhenDebugIsDisabled_WritesNothing()
    {
        var logger = new DisabledDebugLogger();
        WebApiLogFormatter formatter = new WebApiLogFormatter(Settings(true, 100), logger);

        formatter.LogRequest(Sending(Encoding.UTF8.GetBytes("x")));
        formatter.LogResponse(Response(200, CommErrorKind.None, Encoding.UTF8.GetBytes("x"), TimeSpan.Zero));

        Assert.Equal(0, logger.LogCalls);
    }

    private static WebApiLogFormatter Create(ILogger logger, bool logBodies = false, int maxLength = 2048)
        => new WebApiLogFormatter(Settings(logBodies, maxLength), logger);

    private static EndpointSettings Settings(bool logBodies, int maxLength)
        => EndpointConfigRules.CreateSettings(new TestEndpointConfig { Logging = new WebApiLogOptions { LogBodies = logBodies, MaxLoggedBodyLength = maxLength } });

    private static WebApiSendingEventArgs Sending(byte[] body)
        => new WebApiSendingEventArgs("mes", 3, "POST", RedactedUri, 2, new Dictionary<string, string>(), body, "application/json");

    private static WebApiResponseEventArgs Response(int? status, CommErrorKind kind, byte[] body, TimeSpan elapsed)
        => new WebApiResponseEventArgs("mes", 3, "POST", RedactedUri, 2, status, kind, body, elapsed, false, 0, null);

    private sealed class DisabledDebugLogger : ILogger
    {
        public int LogCalls { get; private set; }

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => false;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            => LogCalls++;
    }
}
