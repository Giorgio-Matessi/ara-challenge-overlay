using AraOverlay.Core;

namespace AraOverlay.Core.Tests;

/// <summary>Covers turning telemetry frames into lap events, and what spoils a lap.</summary>
public class LapTrackerTests
{
    /// <summary>Builds one frame, defaulting what isn't the point of the test.</summary>
    /// <param name="lap">The lap counter.</param>
    /// <param name="lastLapTime">LapLastLapTime; -1 means no lap yet.</param>
    /// <param name="surface">PlayerTrackSurface.</param>
    /// <param name="incidents">The running incident count.</param>
    /// <param name="onPitRoad">Whether the car is in pit lane.</param>
    /// <returns>The frame.</returns>
    private static TelemetryFrame Frame(
        int lap,
        double lastLapTime = -1,
        int surface = TrackSurface.OnTrack,
        int incidents = 0,
        bool onPitRoad = false)
        => new(lap, lastLapTime, surface, incidents, onPitRoad);

    /// <summary>Pushes frames through the tracker.</summary>
    /// <param name="tracker">The tracker under test.</param>
    /// <param name="frames">The frames, in order.</param>
    /// <returns>Everything it emitted.</returns>
    private static List<LapEvent> Pump(LapTracker tracker, params TelemetryFrame[] frames)
    {
        var events = new List<LapEvent>();
        foreach (var frame in frames)
            if (tracker.Update(frame) is { } e)
                events.Add(e);
        return events;
    }

    [Fact]
    public void FirstFrameOnlySeedsState()
    {
        Assert.Null(new LapTracker().Update(Frame(1)));
    }

    [Fact]
    public void CleanLapEmitsCompletedExactlyOnce()
    {
        var tracker = new LapTracker();
        var events = Pump(tracker,
            Frame(1),
            Frame(1),
            Frame(2),
            Frame(2, 54.321),
            Frame(2, 54.321),
            Frame(2, 54.321));

        var e = Assert.Single(events);
        Assert.Equal(LapOutcome.Completed, e.Outcome);
        Assert.Equal(54.321, e.Seconds, 3);
    }

    [Fact]
    public void LapTimeArrivingSeveralFramesLateStillEmitsOnce()
    {
        var tracker = new LapTracker();
        var events = Pump(tracker,
            Frame(1),
            Frame(2), Frame(2), Frame(2), Frame(2),
            Frame(2, 54.321));

        Assert.Equal(LapOutcome.Completed, Assert.Single(events).Outcome);
    }

    [Fact]
    public void GoingOffTrackInvalidatesTheLap()
    {
        var tracker = new LapTracker();
        var events = Pump(tracker,
            Frame(1),
            Frame(1, surface: TrackSurface.OffTrack),
            Frame(1),
            Frame(2),
            Frame(2, 54.321));

        var e = Assert.Single(events);
        Assert.Equal(LapOutcome.Invalidated, e.Outcome);
        Assert.Contains("off track", e.Reason, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(54.321, e.Seconds, 3);
    }

    [Fact]
    public void PickingUpAnIncidentInvalidatesTheLap()
    {
        var tracker = new LapTracker();
        var events = Pump(tracker,
            Frame(1, incidents: 2),
            Frame(1, incidents: 6),
            Frame(2, incidents: 6),
            Frame(2, 54.321, incidents: 6));

        var e = Assert.Single(events);
        Assert.Equal(LapOutcome.Invalidated, e.Outcome);
        Assert.Contains("incident", e.Reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void EnteringPitLaneInvalidatesTheLap()
    {
        var tracker = new LapTracker();
        var events = Pump(tracker,
            Frame(1),
            Frame(1, onPitRoad: true),
            Frame(2),
            Frame(2, 54.321));

        var e = Assert.Single(events);
        Assert.Equal(LapOutcome.Invalidated, e.Outcome);
        Assert.Contains("pit", e.Reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void DirtinessClearsForTheFollowingLap()
    {
        var tracker = new LapTracker();
        var events = Pump(tracker,
            Frame(1),
            Frame(1, surface: TrackSurface.OffTrack),
            Frame(2),
            Frame(2, 60.000),
            Frame(3, 60.000),
            Frame(3, 54.321));

        Assert.Equal(2, events.Count);
        Assert.Equal(LapOutcome.Invalidated, events[0].Outcome);
        Assert.Equal(LapOutcome.Completed, events[1].Outcome);
        Assert.Equal(54.321, events[1].Seconds, 3);
    }

    [Fact]
    public void MistakeAfterCrossingTheLineDoesNotSpoilTheLapJustFinished()
    {
        var tracker = new LapTracker();
        var events = Pump(tracker,
            Frame(1),
            Frame(2),
            Frame(2, surface: TrackSurface.OffTrack),
            Frame(2, 54.321));

        Assert.Equal(LapOutcome.Completed, Assert.Single(events).Outcome);
        Assert.False(tracker.CurrentLapIsClean);
    }

    [Fact]
    public void ALapTimeThatNeverArrivesIsDroppedAndTheTrackerRecovers()
    {
        var tracker = new LapTracker();
        var frames = new List<TelemetryFrame> { Frame(1), Frame(2) };
        for (var i = 0; i < 300; i++) frames.Add(Frame(2));

        var events = Pump(tracker, frames.ToArray());
        Assert.Empty(events);

        // The next lap still works.
        events = Pump(tracker, Frame(3), Frame(3, 54.321));
        Assert.Equal(LapOutcome.Completed, Assert.Single(events).Outcome);
    }

    [Fact]
    public void LapCounterGoingBackwardsDropsPendingState()
    {
        var tracker = new LapTracker();
        var events = Pump(tracker,
            Frame(5),
            Frame(6),
            Frame(1),
            Frame(1, 54.321));

        Assert.Empty(events);
    }

    [Fact]
    public void ResetDropsPendingState()
    {
        var tracker = new LapTracker();
        Pump(tracker, Frame(1), Frame(2));
        tracker.Reset();

        Assert.Empty(Pump(tracker, Frame(2, 54.321)));
        Assert.True(tracker.CurrentLapIsClean);
    }

    [Fact]
    public void CurrentLapIsCleanTracksTheLapInProgress()
    {
        var tracker = new LapTracker();
        Pump(tracker, Frame(1));
        Assert.True(tracker.CurrentLapIsClean);

        Pump(tracker, Frame(1, surface: TrackSurface.OffTrack));
        Assert.False(tracker.CurrentLapIsClean);

        Pump(tracker, Frame(2), Frame(2, 54.321));
        Assert.True(tracker.CurrentLapIsClean);
    }

    [Fact]
    public void SessionBestStartsEmpty()
    {
        Assert.Null(new LapTracker().SessionBestSeconds);
    }

    [Fact]
    public void SessionBestTakesTheQuickestCleanLap()
    {
        var tracker = new LapTracker();
        Pump(tracker,
            Frame(1),
            Frame(2), Frame(2, 55.000),
            Frame(3), Frame(3, 54.100),
            Frame(4), Frame(4, 54.800));

        Assert.Equal(54.100, tracker.SessionBestSeconds!.Value, 3);
    }

    [Fact]
    public void SessionBestIgnoresAnInvalidatedLap()
    {
        var tracker = new LapTracker();
        Pump(tracker,
            Frame(1),
            Frame(1, surface: TrackSurface.OffTrack),
            Frame(2), Frame(2, 51.000),
            Frame(3), Frame(3, 54.500));

        Assert.Equal(54.500, tracker.SessionBestSeconds!.Value, 3);
    }

    [Fact]
    public void ResetClearsSessionBest()
    {
        var tracker = new LapTracker();
        Pump(tracker, Frame(1), Frame(2), Frame(2, 54.321));
        Assert.NotNull(tracker.SessionBestSeconds);

        tracker.Reset();

        Assert.Null(tracker.SessionBestSeconds);
    }
}
