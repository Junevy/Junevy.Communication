using Junevy.Communication.Core.Results;
using Junevy.Communication.Testing;
using Junevy.Communication.WebApi.Http;
using Microsoft.Extensions.Logging;

namespace Junevy.Communication.WebApi.Tests;

/// <summary>
/// <see cref="AvailabilityTracker"/> 的转换表测试（P3-D8 / D13，计划 Task 7）：哪些结果算失败、阈值、重置，以及事件内容。
/// </summary>
public sealed class AvailabilityTrackerTests
{
    private const int NoStatus = -1;

    [Fact]
    public void InitialState_IsUnknown()
    {
        Assert.Equal(WebApiAvailability.Unknown, Tracker(3, out _).Availability);
    }

    [Theory]
    [InlineData(200)]
    [InlineData(201)]
    [InlineData(204)]
    [InlineData(301)]
    [InlineData(400)]
    [InlineData(401)]
    [InlineData(404)]
    [InlineData(408)]
    [InlineData(429)]
    [InlineData(500)]
    [InlineData(501)]
    public void AnyResponseOtherThan502503504_MakesTheEndpointOnline(int status)
    {
        AvailabilityTracker tracker = Tracker(3, out List<WebApiAvailabilityChangedEventArgs> changes);

        tracker.ReportAttempt(CommErrorKind.None, status, null);

        Assert.Equal(WebApiAvailability.Online, tracker.Availability);
        WebApiAvailabilityChangedEventArgs change = Assert.Single(changes);
        Assert.Equal(WebApiAvailability.Unknown, change.PreviousAvailability);
        Assert.Equal(WebApiAvailability.Online, change.CurrentAvailability);
        Assert.Equal(CommErrorKind.None, change.Reason);
        Assert.Equal("mes", change.EndpointName);
    }

    [Fact]
    public void OversizedResponse_StillMeansTheEndpointAnswered()
    {
        AvailabilityTracker tracker = Tracker(3, out _);

        tracker.ReportAttempt(CommErrorKind.ProtocolViolation, 200, null);

        Assert.Equal(WebApiAvailability.Online, tracker.Availability);
    }

    [Theory]
    [InlineData(CommErrorKind.ConnectionClosed, NoStatus)]
    [InlineData(CommErrorKind.Timeout, NoStatus)]
    [InlineData(CommErrorKind.None, 502)]
    [InlineData(CommErrorKind.None, 503)]
    [InlineData(CommErrorKind.None, 504)]
    public void ConsecutiveFailures_ReachingTheThreshold_MakeTheEndpointOffline(CommErrorKind kind, int status)
    {
        AvailabilityTracker tracker = Tracker(3, out List<WebApiAvailabilityChangedEventArgs> changes);

        tracker.ReportAttempt(kind, ToStatus(status), null);
        tracker.ReportAttempt(kind, ToStatus(status), null);
        Assert.Equal(WebApiAvailability.Unknown, tracker.Availability);
        Assert.Empty(changes);
        tracker.ReportAttempt(kind, ToStatus(status), null);

        Assert.Equal(WebApiAvailability.Offline, tracker.Availability);
        Assert.Single(changes);
    }

    [Fact]
    public void Threshold1_OneFailureIsEnough_FromUnknownAndFromOnline()
    {
        AvailabilityTracker fromUnknown = Tracker(1, out _);
        fromUnknown.ReportAttempt(CommErrorKind.ConnectionClosed, null, null);
        AvailabilityTracker fromOnline = Tracker(1, out List<WebApiAvailabilityChangedEventArgs> changes);
        fromOnline.ReportAttempt(CommErrorKind.None, 200, null);
        fromOnline.ReportAttempt(CommErrorKind.Timeout, null, null);

        Assert.Equal(WebApiAvailability.Offline, fromUnknown.Availability);
        Assert.Equal(WebApiAvailability.Offline, fromOnline.Availability);
        Assert.Equal(WebApiAvailability.Online, changes[1].PreviousAvailability);
        Assert.Equal(WebApiAvailability.Offline, changes[1].CurrentAvailability);
    }

    [Fact]
    public void FewerFailuresThanTheThreshold_DoNotChangeAnOnlineEndpoint()
    {
        AvailabilityTracker tracker = Tracker(3, out List<WebApiAvailabilityChangedEventArgs> changes);
        tracker.ReportAttempt(CommErrorKind.None, 200, null);

        tracker.ReportAttempt(CommErrorKind.ConnectionClosed, null, null);
        tracker.ReportAttempt(CommErrorKind.ConnectionClosed, null, null);

        Assert.Equal(WebApiAvailability.Online, tracker.Availability);
        Assert.Single(changes);
    }

    [Fact]
    public void ASuccessInBetween_ResetsTheFailureCount()
    {
        AvailabilityTracker tracker = Tracker(3, out List<WebApiAvailabilityChangedEventArgs> changes);
        tracker.ReportAttempt(CommErrorKind.ConnectionClosed, null, null);
        tracker.ReportAttempt(CommErrorKind.ConnectionClosed, null, null);
        tracker.ReportAttempt(CommErrorKind.None, 200, null);

        tracker.ReportAttempt(CommErrorKind.ConnectionClosed, null, null);
        tracker.ReportAttempt(CommErrorKind.ConnectionClosed, null, null);

        Assert.Equal(WebApiAvailability.Online, tracker.Availability);
        Assert.Single(changes);
    }

    [Fact]
    public void OfflineThenSuccess_ReturnsToOnline_AndFurtherFailuresWhileOfflineRaiseNothing()
    {
        AvailabilityTracker tracker = Tracker(1, out List<WebApiAvailabilityChangedEventArgs> changes);
        tracker.ReportAttempt(CommErrorKind.Timeout, null, null);
        tracker.ReportAttempt(CommErrorKind.Timeout, null, null);
        tracker.ReportAttempt(CommErrorKind.ConnectionClosed, null, null);
        Assert.Single(changes);

        tracker.ReportAttempt(CommErrorKind.None, 200, null);

        Assert.Equal(WebApiAvailability.Online, tracker.Availability);
        Assert.Equal(2, changes.Count);
        Assert.Equal(WebApiAvailability.Offline, changes[1].PreviousAvailability);
        Assert.Equal(WebApiAvailability.Online, changes[1].CurrentAvailability);
        Assert.Equal(CommErrorKind.None, changes[1].Reason);
    }

    [Theory]
    [InlineData(CommErrorKind.Cancelled)]
    [InlineData(CommErrorKind.InvalidRequest)]
    public void CancellationAndInvalidRequests_NeitherCountNorReset(CommErrorKind ignored)
    {
        AvailabilityTracker tracker = Tracker(2, out List<WebApiAvailabilityChangedEventArgs> changes);
        tracker.ReportAttempt(CommErrorKind.ConnectionClosed, null, null);

        tracker.ReportAttempt(ignored, null, null);
        tracker.ReportAttempt(ignored, 200, null);
        Assert.Empty(changes);
        tracker.ReportAttempt(CommErrorKind.ConnectionClosed, null, null);

        Assert.Equal(WebApiAvailability.Offline, tracker.Availability);
    }

    [Fact]
    public void OfflineEvent_CarriesTheLastFailureKindAndException()
    {
        AvailabilityTracker tracker = Tracker(2, out List<WebApiAvailabilityChangedEventArgs> changes);
        var last = new IOException("reset");

        tracker.ReportAttempt(CommErrorKind.Timeout, null, new TimeoutException());
        tracker.ReportAttempt(CommErrorKind.ConnectionClosed, null, last);

        WebApiAvailabilityChangedEventArgs change = Assert.Single(changes);
        Assert.Equal(CommErrorKind.ConnectionClosed, change.Reason);
        Assert.Same(last, change.Exception);
    }

    [Fact]
    public void OfflineBecauseOfA503_ReportsRemoteErrorAsTheReason()
    {
        AvailabilityTracker tracker = Tracker(1, out List<WebApiAvailabilityChangedEventArgs> changes);

        tracker.ReportAttempt(CommErrorKind.None, 503, null);

        Assert.Equal(CommErrorKind.RemoteError, Assert.Single(changes).Reason);
    }

    [Fact]
    public void EveryChange_IsLoggedAtInformationLevel()
    {
        var logger = new TestLogger();
        var tracker = new AvailabilityTracker("mes", 1, _ => { }, logger);

        tracker.ReportAttempt(CommErrorKind.ConnectionClosed, null, null);
        tracker.ReportAttempt(CommErrorKind.None, 200, null);

        Assert.Equal(2, logger.GetEntries(LogLevel.Information).Count);
        Assert.Contains("Offline", logger.GetEntries(LogLevel.Information)[0].Message);
    }

    [Fact]
    public async Task ConcurrentReports_PublishAConsistentChainOfChanges()
    {
        var chain = new List<WebApiAvailabilityChangedEventArgs>();
        var tracker = new AvailabilityTracker("mes", 2, chain.Add, new TestLogger());
        var tasks = Enumerable.Range(0, 400).Select(i => Task.Run(() =>
        {
            if (i % 5 == 0)
                tracker.ReportAttempt(CommErrorKind.None, 200, null);
            else
                tracker.ReportAttempt(CommErrorKind.ConnectionClosed, null, null);
        }));

        await Task.WhenAll(tasks);

        WebApiAvailability expectedPrevious = WebApiAvailability.Unknown;
        foreach (WebApiAvailabilityChangedEventArgs change in chain)
        {
            Assert.Equal(expectedPrevious, change.PreviousAvailability);
            Assert.NotEqual(change.PreviousAvailability, change.CurrentAvailability);
            expectedPrevious = change.CurrentAvailability;
        }

        Assert.Equal(tracker.Availability, expectedPrevious);
    }

    private static int? ToStatus(int status) => status == NoStatus ? (int?)null : status;

    private static AvailabilityTracker Tracker(int threshold, out List<WebApiAvailabilityChangedEventArgs> changes)
    {
        var list = new List<WebApiAvailabilityChangedEventArgs>();
        changes = list;
        return new AvailabilityTracker("mes", threshold, list.Add, new TestLogger());
    }
}
