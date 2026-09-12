using System.IO;
using System.Threading;
using System.Threading.Tasks;
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

    /// <summary>Long enough that a fault which repeats immediately can't spin the restart.</summary>
    private static readonly TimeSpan RestartDelay = TimeSpan.FromSeconds(2);

    private string _lastLogged = "";
    private int _restarting;

    /// <summary>True while iRacing is running and handing us telemetry.</summary>
    public bool Connected { get; private set; }

    /// <summary>iRacing's numeric ids for the current session — what a challenge matches on.</summary>
    public int TrackId { get; private set; }
    public int CarId { get; private set; }

    /// <summary>The same track and car by name, so the unmatched panel is readable.</summary>
    public string TrackName { get; private set; } = "";
    public string CarName { get; private set; } = "";

    /// <summary>True when the session counts as wet — challenges 16-20 need this to match.</summary>
    public bool IsWet => _conditions.IsWet;

    /// <summary>The challenge for this track, car and conditions, or null if there isn't one.</summary>
    public Challenge? Challenge { get; private set; }

    public event Action? StateChanged;
    public event Action<double, bool, string>? Tick;   // current lap seconds, clean, reason
    public event Action<LapEvent>? LapFinished;

    public void Start()
    {
        _sdk.OnException += HandleException;
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

    /// <summary>
    /// Each IRSDKSharper loop is shaped <c>try { while (alive) { ... } } catch</c>, so one exception
    /// ends that loop for the life of the process. The telemetry loop is the only thing that raises
    /// OnConnected, so if it dies the overlay never reappears until the app is restarted — which is
    /// exactly what a driver sees after a few session changes. Restart the SDK instead.
    /// </summary>
    private void HandleException(Exception e)
    {
        Log(e);

        // Only one restart at a time: a single fault can kill more than one loop.
        if (Interlocked.Exchange(ref _restarting, 1) == 1) return;

        // Never from this thread. Stop() joins the loop thread that is calling us, and Start()
        // waits for Stop() to finish, so doing it here deadlocks both.
        Task.Run(async () =>
        {
            await Task.Delay(RestartDelay);
            try
            {
                _sdk.Stop();
                _sdk.Start();   // handlers survive Stop(), so this must not go through our Start()
            }
            catch (Exception restartFailure)
            {
                Log(restartFailure);
            }
            Interlocked.Exchange(ref _restarting, 0);
        });
    }

    /// <summary>
    /// Appends to %APPDATA%\AraOverlay\errors.log. Repeats are dropped, because a fault that
    /// recurs every telemetry frame would otherwise write sixty lines a second.
    /// </summary>
    private void Log(Exception e)
    {
        var summary = $"{e.GetType().Name}: {e.Message}";
        if (summary == _lastLogged) return;
        _lastLogged = summary;

        try
        {
            var path = JsonFile.PathIn("errors.log");
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.AppendAllText(path,
                $"{DateTime.Now:s}  {summary}{Environment.NewLine}{e.StackTrace}{Environment.NewLine}");
        }
        catch (Exception logFailure) when (logFailure is IOException or UnauthorizedAccessException)
        {
            // A log we can't write is not worth taking the overlay down for.
        }
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
        TrackId = CarId = 0;
        TrackName = CarName = "";
        _tracker.Reset();
        _conditions.Reset();
        StateChanged?.Invoke();
    }

    // Both handlers below are called from inside IRSDKSharper's loops, within the try that keeps
    // each loop alive. Anything that escapes one of them ends that loop for good, so they swallow
    // and log rather than throw.

    private void HandleSessionInfo()
    {
        try
        {
            var info = _sdk.Data.SessionInfo;
            var me = info.DriverInfo.Drivers.FirstOrDefault(d => d.CarIdx == info.DriverInfo.DriverCarIdx);

            var track = info.WeekendInfo.TrackID;
            var car = me?.CarID ?? 0;

            // Names are only for the unmatched panel, so they follow whatever the ids say.
            TrackName = info.WeekendInfo.TrackName ?? "";
            CarName = me?.CarPath ?? "";

            if (track == TrackId && car == CarId) return;

            TrackId = track;
            CarId = car;
            _tracker.Reset();
            Rematch();
        }
        catch (Exception e)
        {
            // A read that fails leaves the previous match alone — IRSDKSharper schedules a retry,
            // and clearing the panel for one bad read would only flicker.
            Log(e);
        }
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
        try
        {
            var frame = new TelemetryFrame(
                Lap: _sdk.Data.GetInt("Lap"),
                LapLastLapTime: _sdk.Data.GetFloat("LapLastLapTime"),
                TrackSurface: _sdk.Data.GetInt("PlayerTrackSurface"),
                IncidentCount: _sdk.Data.GetInt("PlayerCarMyIncidentCount"),
                OnPitRoad: _sdk.Data.GetBool("OnPitRoad"));

            var currentLap = _sdk.Data.GetFloat("LapCurrentLapTime");

            if (_conditions.Update(_sdk.Data.GetInt("TrackWetness"), _sdk.Data.GetBool("WeatherDeclaredWet")))
                Rematch();

            var completed = _tracker.Update(frame);

            Tick?.Invoke(currentLap, _tracker.CurrentLapIsClean, _tracker.CurrentLapReason);
            if (completed is { } lap) LapFinished?.Invoke(lap);
        }
        catch (Exception e)
        {
            // Telemetry can be mid-swap between sessions; skip the frame rather than die.
            Log(e);
        }
    }
}
