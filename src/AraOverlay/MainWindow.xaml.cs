using System.Globalization;
using System.IO;
using System.Media;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using AraOverlay.Core;
using Forms = System.Windows.Forms;

using Application = System.Windows.Application;
using Color = System.Windows.Media.Color;

namespace AraOverlay;

/// <summary>
/// The overlay window and its tray icon. Shows the active challenge's target times, the live lap,
/// and a banner when a lap earns a new medal; hides itself when iRacing isn't running, and lists
/// the ids the sim reported when the track and car match no challenge.
/// </summary>
public partial class MainWindow : Window
{
    private const int GWL_EXSTYLE = -20;
    private const int WS_EX_TOOLWINDOW = 0x00000080;
    private const int WS_EX_TRANSPARENT = 0x00000020;
    private const int WS_EX_NOACTIVATE = 0x08000000;

    [DllImport("user32.dll")] private static extern int GetWindowLong(IntPtr hWnd, int index);
    [DllImport("user32.dll")] private static extern int SetWindowLong(IntPtr hWnd, int index, int value);

    private readonly Settings _settings = Settings.Load();
    private readonly ProgressStore _progress = new(JsonFile.PathIn("progress.json"));
    private readonly SdkService _sdk = new();
    private readonly DispatcherTimer _bannerTimer = new() { Interval = TimeSpan.FromSeconds(8) };

    private Forms.NotifyIcon? _tray;
    private Forms.ToolStripMenuItem? _lockItem;
    private IntPtr _hwnd;
    private double? _lastLap;
    private bool _demo;

    /// <summary>Restores the saved position and marshals the SDK's events onto the UI thread.</summary>
    public MainWindow()
    {
        InitializeComponent();

        Left = _settings.Left;
        Top = _settings.Top;

        _bannerTimer.Tick += (_, _) => HideBanner();

        _sdk.StateChanged += () => Dispatcher.InvokeAsync(RenderState);
        _sdk.Tick += live => Dispatcher.InvokeAsync(() => OnTick(live));
        _sdk.LapFinished += lap => Dispatcher.InvokeAsync(() => OnLapFinished(lap));
    }

    /// <summary>Applies the window styles and tray icon, then starts the SDK or the demo.</summary>
    /// <param name="e">Unused.</param>
    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        _hwnd = new WindowInteropHelper(this).Handle;
        ApplyWindowStyles();
        BuildTrayIcon();

        if (Environment.GetCommandLineArgs().Contains("--demo")) StartDemo();
        else _sdk.Start();
    }

    /// <summary>Sets click-through for the current lock state and colours the panel border.</summary>
    private void ApplyWindowStyles()
    {
        var style = GetWindowLong(_hwnd, GWL_EXSTYLE) | WS_EX_TOOLWINDOW;

        if (_settings.Locked) style |= WS_EX_TRANSPARENT | WS_EX_NOACTIVATE;
        else style &= ~(WS_EX_TRANSPARENT | WS_EX_NOACTIVATE);

        SetWindowLong(_hwnd, GWL_EXSTYLE, style);

        Panel.BorderBrush = _settings.Locked
            ? new SolidColorBrush(Color.FromArgb(0x20, 0xFF, 0xFF, 0xFF))
            : new SolidColorBrush(Color.FromArgb(0xFF, 0x4C, 0xC2, 0xFF));
    }

    /// <summary>Drags the overlay while unlocked, saving where it lands.</summary>
    /// <param name="sender">The panel that was pressed.</param>
    /// <param name="e">Unused.</param>
    private void OnPanelDrag(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (_settings.Locked) return;

        try
        {
            DragMove();
        }
        catch (InvalidOperationException)
        {
            return;
        }

        _settings.Left = Left;
        _settings.Top = Top;
        _settings.Save();
    }

    /// <summary>Flips click-through on or off and saves the choice.</summary>
    private void ToggleLock()
    {
        _settings.Locked = !_settings.Locked;
        _settings.Save();
        ApplyWindowStyles();
        if (_lockItem is not null) _lockItem.Checked = _settings.Locked;
    }

    /// <summary>Builds the tray icon and its menu.</summary>
    private void BuildTrayIcon()
    {
        _lockItem = new Forms.ToolStripMenuItem("Lock position (click-through)", null, (_, _) => ToggleLock())
        {
            Checked = _settings.Locked,
        };

        var menu = new Forms.ContextMenuStrip();
        menu.Items.Add(_lockItem);
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add(new Forms.ToolStripMenuItem("Exit", null, (_, _) => Quit()));

        _tray = new Forms.NotifyIcon
        {
            Icon = TrayIcon(),
            Text = "ARA Challenge Overlay",
            Visible = true,
            ContextMenuStrip = menu,
        };
    }

    private static readonly Medal[] DemoTiers = [Medal.None, Medal.Bronze, Medal.Silver, Medal.Gold];

    /// <summary>Invents a lap time for the demo.</summary>
    /// <param name="challenge">The challenge being shown.</param>
    /// <param name="medal">The tier the lap should earn.</param>
    /// <returns>A time just inside that tier's threshold.</returns>
    private static double DemoLap(Challenge challenge, Medal medal) => challenge.TargetFor(medal) - 0.05;

    /// <summary>Cycles every challenge and medal state with no sim attached, for --demo.</summary>
    private void StartDemo()
    {
        _demo = true;

        var challenges = ChallengeCatalog.Embedded.Challenges.OrderBy(c => c.Number).ToList();
        var step = 0;

        var demo = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2.5) };
        demo.Tick += (_, _) =>
        {
            var index = step / 2 % challenges.Count;
            var challenge = challenges[index];
            var medal = DemoTiers[index % DemoTiers.Length];
            var lap = medal == Medal.None ? challenge.BronzeSeconds + 1.234 : DemoLap(challenge, medal);

            if (step % 2 == 0)
            {
                HideBanner();
                Root.Visibility = Visibility.Visible;

                _lastLap = lap;
                ShowChallenge(challenge, medal);

                SessionBestText.Text = TimeFormat.Format(lap - 0.35);
                EstLapText.Text = TimeFormat.Format(lap + 0.12);
            }
            else if (medal != Medal.None)
            {
                ShowBanner(medal, lap, challenge);
            }

            step++;
        };

        demo.Start();

        Root.Visibility = Visibility.Visible;
        ShowUnmatched("track  0  \ncar    0  \ncond   dry");
    }

    /// <summary>Loads the tray icon at the size Windows wants.</summary>
    /// <returns>The embedded icon, falling back to the exe's own and then the stock blue "i".</returns>
    private static System.Drawing.Icon TrayIcon()
    {
        try
        {
            using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("AraOverlay.ico");
            if (stream is not null)
                return new System.Drawing.Icon(stream, Forms.SystemInformation.SmallIconSize);

            var exe = Environment.ProcessPath;
            if (exe is not null && System.Drawing.Icon.ExtractAssociatedIcon(exe) is { } fromExe)
                return fromExe;
        }
        catch (Exception e) when (e is ArgumentException or IOException)
        {
        }

        return System.Drawing.SystemIcons.Information;
    }

    /// <summary>Redraws for the current session: hidden, the challenge panel, or the ids.</summary>
    private void RenderState()
    {
        _lastLap = null;
        HideBanner();

        if (!_sdk.Connected)
        {
            Root.Visibility = Visibility.Collapsed;
            return;
        }

        Root.Visibility = Visibility.Visible;

        if (_sdk.Challenge is { } challenge)
        {
            ShowChallenge(challenge);
        }
        else
        {
            ShowUnmatched($"track  {_sdk.TrackId}  {_sdk.TrackName}\n" +
                          $"car    {_sdk.CarId}  {_sdk.CarName}\n" +
                          $"cond   {(_sdk.IsWet ? "wet" : "dry")}");
        }
    }

    /// <summary>Shows the ids the sim reported, for a track and car matching no challenge.</summary>
    /// <param name="detail">The lines to print under the title.</param>
    private void ShowUnmatched(string detail)
    {
        TitleText.Text = "NO ARA CHALLENGE FOR THIS COMBINATION";
        TrackText.Visibility = CarText.Visibility = Visibility.Collapsed;
        HeaderDivider1.Visibility = HeaderDivider2.Visibility = Visibility.Collapsed;
        GoalRow.Visibility = DeltaRow.Visibility = Visibility.Collapsed;
        LapRow.Visibility = Targets.Visibility = Visibility.Collapsed;
        StatusText.Visibility = Visibility.Visible;
        StatusText.Text = detail;
    }

    /// <summary>Fills every row of the panel for a challenge.</summary>
    /// <param name="challenge">The challenge to show.</param>
    /// <param name="held">Which medals to treat as held; defaults to the stored progress.</param>
    private void ShowChallenge(Challenge challenge, Medal? held = null)
    {
        TitleText.Text = $"CHALLENGE {challenge.Number}{(challenge.Wet ? "  ·  WET" : "")}";
        TrackText.Text = challenge.Track.ToUpperInvariant();
        CarText.Text = challenge.Car.ToUpperInvariant();

        TrackText.Visibility = CarText.Visibility = Visibility.Visible;
        HeaderDivider1.Visibility = HeaderDivider2.Visibility = Visibility.Visible;
        GoalRow.Visibility = DeltaRow.Visibility = Visibility.Visible;
        LapRow.Visibility = Targets.Visibility = Visibility.Visible;
        StatusText.Visibility = Visibility.Collapsed;

        GoldTime.Text = TimeFormat.Format(challenge.GoldSeconds);
        SilverTime.Text = TimeFormat.Format(challenge.SilverSeconds);
        BronzeTime.Text = TimeFormat.Format(challenge.BronzeSeconds);

        UpdateHeldMarks(challenge, held);
        UpdateGoalAndDelta(challenge, held);
        UpdateLapRow();
    }

    /// <summary>
    /// Sets the goal tier and the big delta between the last lap and that tier's target. Once
    /// gold is held the goal stays gold, so the delta keeps meaning something.
    /// </summary>
    /// <param name="challenge">The challenge being shown.</param>
    /// <param name="held">Which medal to treat as held; defaults to the stored progress.</param>
    private void UpdateGoalAndDelta(Challenge challenge, Medal? held = null)
    {
        var medal = held ?? _progress.Get(challenge.Number)?.BestMedal ?? Medal.None;
        var goal = Challenge.NextTierAbove(medal) ?? Medal.Gold;

        GoalTier.Text = goal.ToString().ToUpperInvariant();
        GoalTier.Foreground = (SolidColorBrush)FindResource(goal.ToString());
        GoalTime.Text = TimeFormat.Format(challenge.TargetFor(goal));

        if (_lastLap is not { } lap)
        {
            DeltaText.Text = "—";
            DeltaText.Foreground = (SolidColorBrush)FindResource("Dim");
            return;
        }

        var delta = lap - challenge.TargetFor(goal);
        var behind = delta > 0;

        DeltaText.Text = delta.ToString("+0.000;-0.000", CultureInfo.InvariantCulture) + (behind ? "s ▼" : "s ▲");
        DeltaText.Foreground = (SolidColorBrush)FindResource(behind ? "Behind" : "Ahead");
    }

    /// <summary>Sets the last lap and session best times.</summary>
    private void UpdateLapRow()
    {
        LastLapText.Text = _lastLap is { } lap ? TimeFormat.Format(lap) : "—";
        SessionBestText.Text = _sdk.SessionBest is { } best ? TimeFormat.Format(best) : "—";
    }

    /// <summary>Ticks every tier at or below the medal held.</summary>
    /// <param name="challenge">The challenge whose progress to read.</param>
    /// <param name="held">Which medal to treat as held; defaults to the stored progress.</param>
    private void UpdateHeldMarks(Challenge challenge, Medal? held = null)
    {
        var medal = held ?? _progress.Get(challenge.Number)?.BestMedal ?? Medal.None;

        GoldMark.Text = medal >= Medal.Gold ? "✓" : "";
        SilverMark.Text = medal >= Medal.Silver ? "✓" : "";
        BronzeMark.Text = medal >= Medal.Bronze ? "✓" : "";
    }

    /// <summary>
    /// Updates the estimated lap, which doubles as the warning line: a spoiled lap shows what
    /// spoiled it instead of a projection it can no longer earn. Called per frame, so it only
    /// writes on change.
    /// </summary>
    /// <param name="live">The lap in progress.</param>
    private void OnTick(LiveLap live)
    {
        if (_sdk.Challenge is null) return;

        var text = live.Clean
            ? live.EstimatedSeconds is { } estimate ? TimeFormat.Format(estimate) : "—"
            : $"! {live.Reason}";

        if (EstLapText.Text != text)
        {
            EstLapText.Text = text;
            EstLapText.Foreground = (SolidColorBrush)FindResource(live.Clean ? "Ink" : "Behind");
        }
    }

    /// <summary>Records a finished lap and pops the banner if it earned a new tier.</summary>
    /// <param name="lap">The completed or invalidated lap.</param>
    private void OnLapFinished(LapEvent lap)
    {
        if (_sdk.Challenge is not { } challenge) return;

        if (lap.Outcome == LapOutcome.Invalidated)
        {
            LastLapText.Text = $"{TimeFormat.Format(lap.Seconds)}  INVALID";
            return;
        }

        _lastLap = lap.Seconds;

        var medal = challenge.MedalFor(lap.Seconds);
        var earnedNewTier = _progress.RecordLap(challenge.Number, lap.Seconds, medal);

        UpdateHeldMarks(challenge);
        UpdateGoalAndDelta(challenge);
        UpdateLapRow();

        if (earnedNewTier) ShowBanner(medal, lap.Seconds, challenge);
    }

    /// <summary>Tears down the tray icon and the SDK, then exits. The only shutdown path.</summary>
    private void Quit()
    {
        if (_tray is not null) { _tray.Visible = false; _tray.Dispose(); _tray = null; }
        _sdk.Dispose();
        Application.Current.Shutdown();
    }

    /// <summary>Shows the medal banner in place of the panel, for eight seconds.</summary>
    /// <param name="medal">The medal earned; None shows nothing.</param>
    /// <param name="seconds">The lap time that earned it.</param>
    /// <param name="challenge">The challenge it was set on.</param>
    private void ShowBanner(Medal medal, double seconds, Challenge challenge)
    {
        if (medal == Medal.None) return;

        var brush = (SolidColorBrush)FindResource(medal.ToString());

        BannerTitle.Text = medal.ToString().ToUpperInvariant();
        BannerTitle.Foreground = brush;
        Banner.BorderBrush = brush;
        BannerTime.Text = TimeFormat.Format(seconds);
        BannerSub.Text = $"Challenge {challenge.Number} — {challenge.Track}";

        Banner.Visibility = Visibility.Visible;
        Panel.Visibility = Visibility.Collapsed;

        _bannerTimer.Stop();
        _bannerTimer.Start();
        if (!_demo) SystemSounds.Asterisk.Play();
    }

    /// <summary>Puts the panel back. Safe when no banner is showing.</summary>
    private void HideBanner()
    {
        _bannerTimer.Stop();
        Banner.Visibility = Visibility.Collapsed;
        Panel.Visibility = Visibility.Visible;
    }
}
