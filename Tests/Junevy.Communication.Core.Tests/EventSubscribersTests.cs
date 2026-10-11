using Junevy.Communication.Core.Diagnostics;
using Junevy.Communication.Testing;
using Microsoft.Extensions.Logging;

namespace Junevy.Communication.Core.Tests;

/// <summary>
/// <see cref="EventSubscribers"/> 的逐订阅者派发测试。
/// </summary>
public sealed class EventSubscribersTests
{
    private const string EventName = "SampleEvent";

    [Fact]
    public void InvokeEach_NullHandler_DoesNothing()
    {
        var logger = new TestLogger();

        EventSubscribers.InvokeEach<EventArgs>(null, this, EventArgs.Empty, logger, EventName);

        Assert.Empty(logger.Entries);
    }

    [Fact]
    public void InvokeEach_PassesSenderAndArgsToEverySubscriberInOrder()
    {
        var logger = new TestLogger();
        var args = new EventArgs();
        var calls = new List<string>();
        EventHandler<EventArgs>? handler = null;
        handler += (sender, e) => calls.Add(ReferenceEquals(sender, this) && ReferenceEquals(e, args) ? "first" : "wrong");
        handler += (sender, e) => calls.Add(ReferenceEquals(sender, this) && ReferenceEquals(e, args) ? "second" : "wrong");

        EventSubscribers.InvokeEach(handler, this, args, logger, EventName);

        Assert.Equal(new[] { "first", "second" }, calls);
        Assert.Empty(logger.Entries);
    }

    [Fact]
    public void InvokeEach_SecondSubscriberThrows_OthersStillReceiveAndOneErrorIsLogged()
    {
        var logger = new TestLogger();
        var thrown = new InvalidOperationException("subscriber failure");
        var calls = new List<string>();
        EventHandler<EventArgs>? handler = null;
        handler += (sender, e) => calls.Add("first");
        handler += (sender, e) => throw thrown;
        handler += (sender, e) => calls.Add("third");

        EventSubscribers.InvokeEach(handler, this, EventArgs.Empty, logger, EventName);

        Assert.Equal(new[] { "first", "third" }, calls);
        TestLogEntry entry = Assert.Single(logger.GetEntries(LogLevel.Error));
        Assert.Same(thrown, entry.Exception);
        Assert.Contains(EventName, entry.Message);
    }

    [Fact]
    public void InvokeEach_EveryLoggedFailureNamesTheEvent()
    {
        var logger = new TestLogger();
        EventHandler<EventArgs>? handler = null;
        handler += (sender, e) => throw new InvalidOperationException("one");
        handler += (sender, e) => throw new InvalidOperationException("two");

        EventSubscribers.InvokeEach(handler, this, EventArgs.Empty, logger, EventName);

        IReadOnlyList<TestLogEntry> errors = logger.GetEntries(LogLevel.Error);
        Assert.Equal(2, errors.Count);
        Assert.All(errors, entry => Assert.Contains(EventName, entry.Message));
    }
}
