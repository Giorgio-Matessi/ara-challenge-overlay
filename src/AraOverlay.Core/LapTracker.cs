namespace AraOverlay.Core;

/// <summary>iRacing's irsdk_TrackLocation values, as read from PlayerTrackSurface.</summary>
public static class TrackSurface
{
    public const int OffTrack = 0;
    public const int OnTrack = 3;
}

public readonly record struct TelemetryFrame(
    int Lap,
    double LapLastLapTime,
    int TrackSurface,
    int IncidentCount,
    bool OnPitRoad);

public enum LapOutcome { Completed, Invalidated }

public readonly record struct LapEvent(LapOutcome Outcome, double Seconds, string Reason);

/// <summary>
/// Turns telemetry frames into at most one event per lap. Spoiling is checked before the line
/// crossing, so anything seen on the increment frame is charged to the lap that is ending; and
/// because iRacing increments Lap before LapLastLapTime catches up, the tracker arms a pending
/// state and emits once the time actually changes.
/// </summary>
public sealed class LapTracker
{
    private const int MaxPendingFrames = 180;

    private bool _seeded;
    private int _lap;
    private int _incidents;

    private bool _clean = true;
    private string _reason = "";

    private bool _pending;
    private bool _pendingClean;
    private string _pendingReason = "";
    private double _timeAtIncrement;
    private int _pendingFrames;

    public bool CurrentLapIsClean => _clean;
    public string CurrentLapReason => _reason;

    /// <summary>Drops all state, so the next frame re-seeds.</summary>
    public void Reset()
    {
        _seeded = false;
        _pending = false;
        _clean = true;
        _reason = "";
    }

    /// <summary>Feeds one telemetry frame.</summary>
    /// <param name="frame">The sample; the first after a reset only seeds state.</param>
    /// <returns>An event on the frame a lap resolves, otherwise null.</returns>
    public LapEvent? Update(TelemetryFrame frame)
    {
        if (!_seeded)
        {
            _seeded = true;
            _lap = frame.Lap;
            _incidents = frame.IncidentCount;
            _clean = true;
            _reason = "";
            return null;
        }

        if (frame.IncidentCount > _incidents) Spoil("incident");
        if (frame.TrackSurface == AraOverlay.Core.TrackSurface.OffTrack) Spoil("off track");
        if (frame.OnPitRoad) Spoil("pit lane");
        _incidents = frame.IncidentCount;

        if (frame.Lap > _lap)
        {
            _lap = frame.Lap;

            _pending = true;
            _pendingClean = _clean;
            _pendingReason = _reason;
            _timeAtIncrement = frame.LapLastLapTime;
            _pendingFrames = 0;

            _clean = true;
            _reason = "";
            return null;
        }

        if (frame.Lap < _lap)
        {
            // Towed, reset to garage, or a new session: whatever was pending is meaningless.
            _lap = frame.Lap;
            _pending = false;
            _clean = true;
            _reason = "";
            return null;
        }

        if (!_pending) return null;

        if (frame.LapLastLapTime > 0 && Math.Abs(frame.LapLastLapTime - _timeAtIncrement) > 1e-6)
        {
            _pending = false;
            return _pendingClean
                ? new LapEvent(LapOutcome.Completed, frame.LapLastLapTime, "")
                : new LapEvent(LapOutcome.Invalidated, frame.LapLastLapTime, _pendingReason);
        }

        if (++_pendingFrames > MaxPendingFrames) _pending = false;
        return null;
    }

    /// <summary>Marks the lap in progress invalid, keeping the first reason.</summary>
    /// <param name="reason">What spoiled it.</param>
    private void Spoil(string reason)
    {
        if (!_clean) return;
        _clean = false;
        _reason = reason;
    }
}
