namespace AraOverlay.Core;

/// <summary>iRacing's irsdk_TrackLocation values, as read from PlayerTrackSurface.</summary>
public static class TrackSurface
{
    public const int OffTrack = 0;
    public const int OnTrack = 3;
}

/// <summary>One telemetry sample, reduced to just what lap validity depends on.</summary>
public readonly record struct TelemetryFrame(
    int Lap,
    double LapLastLapTime,
    int TrackSurface,
    int IncidentCount,
    bool OnPitRoad);

public enum LapOutcome { Completed, Invalidated }

public readonly record struct LapEvent(LapOutcome Outcome, double Seconds, string Reason);

/// <summary>
/// Turns a stream of telemetry frames into at most one event per completed lap.
///
/// Two things make this less trivial than "did LapLastLapTime change":
///
/// 1. Validity. The league wants clean laps, so anything that spoils a lap — leaving the
///    track surface, gaining incident points, touching pit lane — is latched until the lap ends.
///
/// 2. Timing. iRacing increments Lap a frame or two *before* LapLastLapTime catches up, so
///    reading the time on the increment frame gives you the previous lap. The tracker arms a
///    pending state on the increment and emits once the time actually changes.
/// </summary>
public sealed class LapTracker
{
    /// <summary>~3 seconds at 60Hz. If the time hasn't landed by then, the lap is dropped.</summary>
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

    /// <summary>False once the lap in progress has been spoiled. Drives the overlay's warning.</summary>
    public bool CurrentLapIsClean => _clean;

    /// <summary>Why the lap in progress is spoiled, or "" while it's still clean.</summary>
    public string CurrentLapReason => _reason;

    public void Reset()
    {
        _seeded = false;
        _pending = false;
        _clean = true;
        _reason = "";
    }

    /// <summary>Feeds one frame. Returns an event on the frame a lap resolves, otherwise null.</summary>
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

        // Spoil first, then look for the line crossing: dirt seen on the increment frame is
        // attributed to the lap that is ending, which is the conservative reading.
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

            _clean = true;          // the new lap starts with a clean sheet
            _reason = "";
            return null;
        }

        if (frame.Lap < _lap)
        {
            // Towed, reset to garage, or a new session: whatever was pending is meaningless now.
            _lap = frame.Lap;
            _pending = false;
            _clean = true;
            _reason = "";
            return null;
        }

        if (!_pending) return null;

        // ponytail: "the time changed" is the completion signal. Two consecutive laps identical
        // to the millisecond would be missed and dropped by the timeout below — acceptable.
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

    private void Spoil(string reason)
    {
        if (!_clean) return;        // keep the first reason; it's the one that actually cost the lap
        _clean = false;
        _reason = reason;
    }
}
