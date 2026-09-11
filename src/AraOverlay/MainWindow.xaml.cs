using System.Globalization;
using System.Media;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using AraOverlay.Core;
using Forms = System.Windows.Forms;

// UseWindowsForms adds System.Windows.Forms and System.Drawing as implicit usings, which
// collide with WPF's types of the same name. This file wants the WPF ones; WinForms is
// reached through the Forms alias above.
using Application = System.Windows.Application;
using Color = System.Windows.Media.Color;

namespace AraOverlay;

public partial class MainWindow : Window
{
    private const int GWL_EXSTYLE = -20;
    private const int WS_EX_TOOLWINDOW = 0x00000080;   // keep it out of alt-tab
    private const int WS_EX_TRANSPARENT = 0x00000020;  // clicks fall through to iRacing
    private const int WS_EX_NOACTIVATE = 0x08000000;   // never steal focus from the sim

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

    public MainWindow()
    {
        InitializeComponent();

        Left = _settings.Left;
        Top = _settings.Top;

        _bannerTimer.Tick += (_, _) => HideBanner();

        // The SDK calls back on its own thread; everything below must run on the UI thread.
        _sdk.StateChanged += () => Dispatcher.InvokeAsync(RenderState);
        _sdk.Tick += (lapSeconds, clean, reason) => Dispatcher.InvokeAsync(() => OnTick(lapSeconds, clean, reason));
        _sdk.LapFinished += lap => Dispatcher.InvokeAsync(() => OnLapFinished(lap));
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        _hwnd = new WindowInteropHelper(this).Handle;
        ApplyWindowStyles();
        BuildTrayIcon();

        if (Environment.GetCommandLineArgs().Contains("--demo")) StartDemo();
        else _sdk.Start();
    }

    // ---- window behaviour -------------------------------------------------

    private void ApplyWindowStyles()
    {
        var style = GetWindowLong(_hwnd, GWL_EXSTYLE) | WS_EX_TOOLWINDOW;

        if (_settings.Locked) style |= WS_EX_TRANSPARENT | WS_EX_NOACTIVATE;
        else style &= ~(WS_EX_TRANSPARENT | WS_EX_NOACTIVATE);

        SetWindowLong(_hwnd, GWL_EXSTYLE, style);

        // Unlocked gets a visible handle so it's obvious the overlay is draggable.
        Panel.BorderBrush = _settings.Locked
            ? new SolidColorBrush(Color.FromArgb(0x20, 0xFF, 0xFF, 0xFF))
            : new SolidColorBrush(Color.FromArgb(0xFF, 0x4C, 0xC2, 0xFF));
    }

    private void OnPanelDrag(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (_settings.Locked) return;   // shouldn't fire while click-through, but belt and braces

        try
        {
            DragMove();                 // returns once the mouse is released
        }
        catch (InvalidOperationException)
        {
            return;                     // button came up before we got here
        }

        _settings.Left = Left;
        _settings.Top = Top;
        _settings.Save();
    }

    private void ToggleLock()
    {
        _settings.Locked = !_settings.Locked;
        _settings.Save();
        ApplyWindowStyles();
        if (_lockItem is not null) _lockItem.Checked = _settings.Locked;
    }

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
            Icon = System.Drawing.SystemIcons.Information,
            Text = "ARA Challenge Overlay",
            Visible = true,
            ContextMenuStrip = menu,
        };
    }

    /// <summary>The four outcomes a lap can have, walked in turn so each one gets rendered.</summary>
    private static readonly Medal[] DemoTiers = [Medal.None, Medal.Bronze, Medal.Silver, Medal.Gold];

    /// <summary>A lap just inside the tier's threshold, so it earns that medal and no better.</summary>
    private static double DemoLap(Challenge challenge, Medal medal) => challenge.TargetFor(medal) - 0.05;

    /// <summary>
    /// Renders every challenge in turn with no sim attached, so the overlay can be looked at —
    /// and its longest names checked for wrapping — on a machine that can't run iRacing.
    /// </summary>
    private void StartDemo()
    {
        _demo = true;   // 20 banners in a row, so the medal chime stays off

        var challenges = ChallengeCatalog.Embedded.Challenges.OrderBy(c => c.Number).ToList();
        var step = 0;

        var demo = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2.5) };
        demo.Tick += (_, _) =>
        {
            var index = step / 2 % challenges.Count;
            var challenge = challenges[index];
            var medal = DemoTiers[index % DemoTiers.Length];

            // Alternate panel and banner, so both states get looked at for every challenge.
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

        // The unmatched panel is the one state with no challenge, so show it first.
        Root.Visibility = Visibility.Visible;
        TitleText.Text = "No ARA challenge for this combination";
        TrackText.Visibility = CarText.Visibility = Visibility.Collapsed;
        Targets.Visibility = Visibility.Collapsed;
        StatusText.Text = "track  0  \ncar    0  \ncond   dry";
    }

    // ---- rendering --------------------------------------------------------

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
            // Conditions are shown too: a wet challenge simply won't match in the dry.
            StatusText.Text = $"track  {_sdk.TrackId}  {_sdk.TrackName}\n" +
                              $"car    {_sdk.CarId}  {_sdk.CarName}\n" +
                              $"cond   {(_sdk.IsWet ? "wet" : "dry")}";
        }
    }

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

    private void UpdateHeldMarks(Challenge challenge, Medal? held = null)
    {
        var medal = held ?? _progress.Get(challenge.Number)?.BestMedal ?? Medal.None;

        GoldMark.Text = medal >= Medal.Gold ? "✓" : "";
        SilverMark.Text = medal >= Medal.Silver ? "✓" : "";
        BronzeMark.Text = medal >= Medal.Bronze ? "✓" : "";
    }

    private void OnTick(double currentLapSeconds, bool clean, string reason)
    {
        if (_sdk.Challenge is null) return;   // the unmatched panel shows ids, not lap times

        var live = currentLapSeconds > 0 ? $"lap  {TimeFormat.Format(currentLapSeconds)}" : "lap  —";
        if (!clean) live += $"   ! {reason}";

        var text = _lastLapLine.Length == 0 ? live : live + "\n" + _lastLapLine;
        if (StatusText.Text != text) StatusText.Text = text;   // 60Hz, so only touch it on change
    }

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

    private void Quit()
    {
        // ShutdownMode is OnExplicitShutdown, so closing the window doesn't end the app and this
        // is the only teardown path. Miss it and the tray icon lingers as a ghost.
        if (_tray is not null) { _tray.Visible = false; _tray.Dispose(); _tray = null; }
        _sdk.Dispose();
        Application.Current.Shutdown();
    }

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
        Panel.Visibility = Visibility.Collapsed;   // swap rather than stack, so nothing jumps

        _bannerTimer.Stop();
        _bannerTimer.Start();
        if (!_demo) SystemSounds.Asterisk.Play();
    }

    private void HideBanner()
    {
        _bannerTimer.Stop();
        Banner.Visibility = Visibility.Collapsed;
        Panel.Visibility = Visibility.Visible;
    }
}
