using System.IO;
using System.Threading;
using System.Threading.Tasks;
using AraOverlay.Core;
using IRSDKSharper;

namespace AraOverlay;

/// <summary>The lap in progress, as the panel needs it.</summary>
public readonly record struct LiveLap(double? EstimatedSeconds, bool Clean, string Reason);

/// <summary>
/// The only place that touches the iRacing SDK. Translates its callbacks into plain events and
/// feeds the trackers; events fire on the SDK's thread, so callers marshal to the UI themselves.
///
/// Each IRSDKSharper loop is written try { while (alive) } catch, so one exception ends it for
/// the life of the process and the overlay never reconnects. Hence the restart below, and the
/// catch-all in the handlers, which run inside that same try.
/// </summary>
public sealed class SdkService : IDisposable
{
    private readonly IRacingSdk _sdk = new();
    private readonly LapTracker _tracker = new();
    private readonly ConditionTracker _conditions = new();
    private ChallengeCatalog _catalog = ChallengeCatalog.Embedded;
    private ChallengeCatalog? _staged;

    private static readonly TimeSpan RestartDelay = TimeSpan.FromSeconds(2);

    private string _lastLogged = "";
    private int _selected;
    private int _restarting;

    public bool Connected { get; private set; }
    public int TrackId { get; private set; }
    public int CarId { get; private set; }
    public string TrackName { get; private set; } = "";
    public string CarName { get; private set; } = "";
    public bool IsWet => _conditions.IsWet;
    public double? SessionBest => _tracker.SessionBestSeconds;
    /// <summary>Every challenge this track and car could be, one per plan.</summary>
    public IReadOnlyList<Challenge> Matches { get; private set; } = [];

    /// <summary>Which of the matches the panel is showing.</summary>
    public Challenge? Challenge => _selected < Matches.Count ? Matches[_selected] : null;

    public event Action? StateChanged;
    public event Action<LiveLap>? Tick;
    public event Action<LapEvent>? LapFinished;

    /// <summary>Subscribes to the SDK and starts its background loops.</summary>
    public void Start()
    {
        _sdk.OnException += HandleException;
        _sdk.OnConnected += HandleConnected;
        _sdk.OnDisconnected += HandleDisconnected;
        _sdk.OnSessionInfo += HandleSessionInfo;
        _sdk.OnTelemetryData += HandleTelemetryData;
        _sdk.Start();
    }

    /// <summary>Stops the SDK, on exit.</summary>
    public void Dispose()
    {
        try { _sdk.Stop(); } catch { }
    }

    /// <summary>Logs a dead SDK loop and restarts the SDK, since nothing else will.</summary>
    /// <param name="e">What the loop threw.</param>
    private void HandleException(Exception e)
    {
        Log(e);

        if (Interlocked.Exchange(ref _restarting, 1) == 1) return;

        // Never from this thread: Stop() joins it and Start() waits for Stop(), which deadlocks.
        Task.Run(async () =>
        {
            await Task.Delay(RestartDelay);
            try
            {
                _sdk.Stop();
                _sdk.Start();
            }
            catch (Exception restartFailure)
            {
                Log(restartFailure);
            }
            Interlocked.Exchange(ref _restarting, 0);
        });
    }

    /// <summary>Logs an exception with its stack trace, dropping repeats.</summary>
    /// <param name="e">The exception to record.</param>
    private void Log(Exception e)
    {
        var summary = $"{e.GetType().Name}: {e.Message}";
        if (summary == _lastLogged) return;
        _lastLogged = summary;

        Log($"{summary}{Environment.NewLine}{e.StackTrace}");
    }

    /// <summary>Appends a line to %APPDATA%\AraOverlay\errors.log. A failed write is dropped.</summary>
    /// <param name="message">What to record.</param>
    public static void Log(string message)
    {
        try
        {
            var path = JsonFile.PathIn("errors.log");
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.AppendAllText(path, $"{DateTime.Now:s}  {message}{Environment.NewLine}");
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
        }
    }

    /// <summary>iRacing started producing data.</summary>
    private void HandleConnected()
    {
        Connected = true;
        _tracker.Reset();
        StateChanged?.Invoke();
    }

    /// <summary>iRacing stopped producing data.</summary>
    private void HandleDisconnected()
    {
        Connected = false;
        Matches = [];
        _selected = 0;
        TrackId = CarId = 0;
        TrackName = CarName = "";
        _tracker.Reset();
        _conditions.Reset();
        StateChanged?.Invoke();
    }

    /// <summary>
    /// Reads the track and car from session info and re-matches if either changed. A failed read
    /// leaves the previous match alone, since the SDK schedules its own retry.
    /// </summary>
    private void HandleSessionInfo()
    {
        try
        {
            var info = _sdk.Data.SessionInfo;
            var me = info.DriverInfo.Drivers.FirstOrDefault(d => d.CarIdx == info.DriverInfo.DriverCarIdx);

            var track = info.WeekendInfo.TrackID;
            var car = me?.CarID ?? 0;

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
            Log(e);
        }
    }

    /// <summary>Projects the lap in progress, from iRacing's delta to its own session best.</summary>
    /// <returns>The projected lap time, or null with no usable reference lap.</returns>
    private double? EstimateLap()
    {
        // Its own try: these three names are still unverified against a live sim, and a bad one
        // here would otherwise cost the whole frame, not just the estimate.
        try
        {
            if (!_sdk.Data.GetBool("LapDeltaToSessionBestLap_OK")) return null;

            var reference = _sdk.Data.GetFloat("LapBestLapTime");
            if (reference <= 0) return null;

            var estimate = reference + _sdk.Data.GetFloat("LapDeltaToSessionBestLap");
            return estimate > 0 ? estimate : null;
        }
        catch (Exception e)
        {
            Log(e);
            return null;
        }
    }

    /// <summary>
    /// Hands over a newly fetched catalog. It is staged rather than applied: swapping targets
    /// under a driver mid-lap would move the numbers on the panel and grade the lap against rows
    /// they never saw. It takes effect at the next moment no challenge is live.
    /// </summary>
    /// <param name="catalog">The catalog to move to.</param>
    public void SwapCatalog(ChallengeCatalog catalog)
    {
        _staged = catalog;
        if (Challenge is null) Rematch();
    }

    /// <summary>
    /// Picks a different one of the current matches, for a track and car more than one plan uses.
    /// </summary>
    /// <param name="index">Which match to show.</param>
    public void SelectMatch(int index)
    {
        if (index < 0 || index >= Matches.Count) return;

        _selected = index;
        StateChanged?.Invoke();
    }

    /// <summary>Re-runs the lookup, for a session change or a flip between wet and dry.</summary>
    private void Rematch()
    {
        if (_staged is { } next && Challenge is null)
        {
            _catalog = next;
            _staged = null;
        }

        var found = _catalog.Find(TrackId, CarId, _conditions.IsWet);

        // Keeps the driver's pick across a wet/dry flip or a session change where the same plans
        // still apply; anything else starts from the first again.
        var keep = Challenge?.Plan;

        Matches = found;
        _selected = keep is null ? 0 : Math.Max(0, found.ToList().FindIndex(c => c.Plan == keep));

        StateChanged?.Invoke();
    }

    /// <summary>
    /// Feeds one frame to the trackers, raising Tick every frame and LapFinished when a lap
    /// resolves. A frame that can't be read is skipped: telemetry can be mid-swap between sessions.
    /// </summary>
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

            if (_conditions.Update(_sdk.Data.GetInt("TrackWetness"), _sdk.Data.GetBool("WeatherDeclaredWet")))
                Rematch();

            var completed = _tracker.Update(frame);

            Tick?.Invoke(new LiveLap(EstimateLap(), _tracker.CurrentLapIsClean, _tracker.CurrentLapReason));
            if (completed is { } lap) LapFinished?.Invoke(lap);
        }
        catch (Exception e)
        {
            Log(e);
        }
    }
}
