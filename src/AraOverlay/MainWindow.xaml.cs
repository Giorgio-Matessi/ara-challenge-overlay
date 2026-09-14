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
    private string _lastLapLine = "";
    private bool _demo;

    /// <summary>Restores the saved position and marshals the SDK's events onto the UI thread.</summary>
    public MainWindow()
    {
        InitializeComponent();

        Left = _settings.Left;
        Top = _settings.Top;

        _bannerTimer.Tick += (_, _) => HideBanner();

        _sdk.StateChanged += () => Dispatcher.InvokeAsync(RenderState);
        _sdk.Tick += (lapSeconds, clean, reason) => Dispatcher.InvokeAsync(() => OnTick(lapSeconds, clean, reason));
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

            if (step % 2 == 0)
            {
                HideBanner();
                Root.Visibility = Visibility.Visible;
                ShowChallenge(challenge, medal);
                StatusText.Text = medal == Medal.None
                    ? $"demo   lap  {TimeFormat.Format(challenge.BronzeSeconds + 1.234)}   no medal"
                    : $"demo   lap  {TimeFormat.Format(DemoLap(challenge, medal))}";
            }
            else if (medal != Medal.None)
            {
                ShowBanner(medal, DemoLap(challenge, medal), challenge);
            }

            step++;
        };

        demo.Start();

        Root.Visibility = Visibility.Visible;
        TitleText.Text = "No ARA challenge for this combination";
        TrackText.Visibility = CarText.Visibility = Visibility.Collapsed;
        Targets.Visibility = Visibility.Collapsed;
        StatusText.Text = "track  0  \ncar    0  \ncond   dry";
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
        _lastLapLine = "";
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
            StatusText.Text = "";
        }
        else
        {
            TitleText.Text = "No ARA challenge for this combination";
            TrackText.Visibility = CarText.Visibility = Visibility.Collapsed;
            Targets.Visibility = Visibility.Collapsed;
            StatusText.Text = $"track  {_sdk.TrackId}  {_sdk.TrackName}\n" +
                              $"car    {_sdk.CarId}  {_sdk.CarName}\n" +
                              $"cond   {(_sdk.IsWet ? "wet" : "dry")}";
        }
    }

    /// <summary>Fills the panel with a challenge's header and target times.</summary>
    /// <param name="challenge">The challenge to show.</param>
    /// <param name="held">Which medals to tick; defaults to the stored progress.</param>
    private void ShowChallenge(Challenge challenge, Medal? held = null)
    {
        TitleText.Text = $"CHALLENGE {challenge.Number}{(challenge.Wet ? "  ·  WET" : "")}";
        TrackText.Text = challenge.Track;
        CarText.Text = challenge.Car;
        TrackText.Visibility = CarText.Visibility = Visibility.Visible;
        Targets.Visibility = Visibility.Visible;
        GoldTime.Text = TimeFormat.Format(challenge.GoldSeconds);
        SilverTime.Text = TimeFormat.Format(challenge.SilverSeconds);
        BronzeTime.Text = TimeFormat.Format(challenge.BronzeSeconds);
        UpdateHeldMarks(challenge, held);
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

    /// <summary>Updates the live lap line. Called per frame, so it only writes on change.</summary>
    /// <param name="currentLapSeconds">The lap in progress; not positive before the first lap.</param>
    /// <param name="clean">Whether the lap is still valid.</param>
    /// <param name="reason">What spoiled it, when it isn't.</param>
    private void OnTick(double currentLapSeconds, bool clean, string reason)
    {
        if (_sdk.Challenge is null) return;

        var live = currentLapSeconds > 0 ? $"lap  {TimeFormat.Format(currentLapSeconds)}" : "lap  —";
        if (!clean) live += $"   ! {reason}";

        var text = _lastLapLine.Length == 0 ? live : live + "\n" + _lastLapLine;
        if (StatusText.Text != text) StatusText.Text = text;
    }

    /// <summary>Records a finished lap and pops the banner if it earned a new tier.</summary>
    /// <param name="lap">The completed or invalidated lap.</param>
    private void OnLapFinished(LapEvent lap)
    {
        if (_sdk.Challenge is not { } challenge) return;

        if (lap.Outcome == LapOutcome.Invalidated)
        {
            _lastLapLine = $"last {TimeFormat.Format(lap.Seconds)}  INVALID ({lap.Reason})";
            return;
        }

        var medal = challenge.MedalFor(lap.Seconds);
        var earnedNewTier = _progress.RecordLap(challenge.Number, lap.Seconds, medal);
        UpdateHeldMarks(challenge);

        var held = _progress.Get(challenge.Number)?.BestMedal ?? Medal.None;
        var chasing = Challenge.NextTierAbove(held);

        _lastLapLine = chasing is { } next
            ? $"last {TimeFormat.Format(lap.Seconds)}  {next.ToString().ToLowerInvariant()} " +
              $"{(lap.Seconds - challenge.TargetFor(next)).ToString("+0.000;-0.000", CultureInfo.InvariantCulture)}"
            : $"last {TimeFormat.Format(lap.Seconds)}  gold held";

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
