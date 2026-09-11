using AraOverlay.Core;
using IRSDKSharper;

namespace AraOverlay;

/// <summary>
/// The only place that touches the iRacing SDK. Translates its callbacks into plain events and
/// feeds <see cref="LapTracker"/>; everything above this line is testable Core code.
///
/// Events fire on the SDK's own thread — callers marshal to the UI thread themselves.
/// </summary>
public sealed class SdkService : IDisposable
{
    private readonly IRacingSdk _sdk = new();
    private readonly LapTracker _tracker = new();
    private readonly ConditionTracker _conditions = new();
    private readonly ChallengeCatalog _catalog = ChallengeCatalog.Embedded;

    /// <summary>True while iRacing is running and handing us telemetry.</summary>
    public bool Connected { get; private set; }

    /// <summary>iRacing's internal ids for the current session, shown when nothing matches.</summary>
    public string TrackId { get; private set; } = "";
    public string CarId { get; private set; } = "";

    /// <summary>True when the session counts as wet — challenges 16-20 need this to match.</summary>
    public bool IsWet => _conditions.IsWet;

    /// <summary>The challenge for this track, car and conditions, or null if there isn't one.</summary>
    public Challenge? Challenge { get; private set; }

    public event Action? StateChanged;
    public event Action<double, bool, string>? Tick;   // current lap seconds, clean, reason
    public event Action<LapEvent>? LapFinished;

    public void Start()
    {
        _sdk.OnConnected += HandleConnected;
        _sdk.OnDisconnected += HandleDisconnected;
        _sdk.OnSessionInfo += HandleSessionInfo;
        _sdk.OnTelemetryData += HandleTelemetryData;
        _sdk.Start();
    }

    public void Dispose()
    {
        try { _sdk.Stop(); } catch { /* shutting down anyway */ }
    }

    private void HandleConnected()
    {
        Connected = true;
        _tracker.Reset();
        StateChanged?.Invoke();
    }

    private void HandleDisconnected()
    {
        Connected = false;
        Challenge = null;
        TrackId = CarId = "";
        _tracker.Reset();
        _conditions.Reset();
        StateChanged?.Invoke();
    }

    private void HandleSessionInfo()
    {
        string track = "", car = "";
        try
        {
            var info = _sdk.Data.SessionInfo;
            track = info.WeekendInfo.TrackName ?? "";

            var me = info.DriverInfo.Drivers.FirstOrDefault(d => d.CarIdx == info.DriverInfo.DriverCarIdx);
            car = me?.CarPath ?? "";
        }
        catch (Exception)
        {
            // A session string we can't read means no match; the overlay just shows nothing.
        }

        if (track == TrackId && car == CarId) return;

        TrackId = track;
        CarId = car;
        _tracker.Reset();
        Rematch();
    }

    /// <summary>
    /// Re-runs the lookup. Called on a session change and whenever the track flips between wet
    /// and dry, since conditions are part of what identifies a challenge.
    /// </summary>
    private void Rematch()
    {
        Challenge = _catalog.Find(TrackId, CarId, _conditions.IsWet);
        StateChanged?.Invoke();
    }

    private void HandleTelemetryData()
    {
        TelemetryFrame frame;
        double currentLap;
        int wetness;
        bool declaredWet;
        try
        {
            frame = new TelemetryFrame(
                Lap: _sdk.Data.GetInt("Lap"),
                LapLastLapTime: _sdk.Data.GetFloat("LapLastLapTime"),
                TrackSurface: _sdk.Data.GetInt("PlayerTrackSurface"),
                IncidentCount: _sdk.Data.GetInt("PlayerCarMyIncidentCount"),
                OnPitRoad: _sdk.Data.GetBool("OnPitRoad"));

            currentLap = _sdk.Data.GetFloat("LapCurrentLapTime");
            wetness = _sdk.Data.GetInt("TrackWetness");
            declaredWet = _sdk.Data.GetBool("WeatherDeclaredWet");
        }
        catch (Exception)
        {
            // Telemetry can be mid-swap between sessions; skip the frame rather than die.
            return;
        }

        if (_conditions.Update(wetness, declaredWet)) Rematch();

        var completed = _tracker.Update(frame);

        Tick?.Invoke(currentLap, _tracker.CurrentLapIsClean, _tracker.CurrentLapReason);
        if (completed is { } lap) LapFinished?.Invoke(lap);
    }
}
