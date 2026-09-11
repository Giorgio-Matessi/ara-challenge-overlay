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
using Clipboard = System.Windows.Clipboard;
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
    private readonly ProgressStore _progress = new(ProgressStore.DefaultPath);
    private readonly SdkService _sdk = new();
    private readonly DispatcherTimer _bannerTimer = new() { Interval = TimeSpan.FromSeconds(8) };

    private Forms.NotifyIcon? _tray;
    private Forms.ToolStripMenuItem? _lockItem;
    private IntPtr _hwnd;
    private string _lastLapLine = "";

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
        _sdk.Start();
    }

    protected override void OnClosed(EventArgs e)
    {
        _sdk.Dispose();
        if (_tray is not null) { _tray.Visible = false; _tray.Dispose(); }
        base.OnClosed(e);
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
        menu.Items.Add(new Forms.ToolStripMenuItem("Copy current track/car ID", null, (_, _) => CopyIds()));
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

    /// <summary>
    /// The calibration knob: puts the sim's real ids on the clipboard so a wrong trackId/carId
    /// in challenges.json can be corrected without guessing.
    /// </summary>
    private void CopyIds()
    {
        try
        {
            Clipboard.SetText(
                $"\"trackIds\": [{_sdk.TrackId}],   // {_sdk.TrackName}\n" +
                $"\"carId\": {_sdk.CarId},   // {_sdk.CarName}\n" +
                (_sdk.IsWet ? "\"wet\": true,\n" : ""));
        }
        catch (COMException)
        {
            // Another process had the clipboard locked. Nothing worth reporting mid-session.
        }
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
            TitleText.Text = $"#{challenge.Number}  {challenge.Name}";
            Targets.Visibility = Visibility.Visible;
            GoldTime.Text = TimeFormat.Format(challenge.GoldSeconds);
            SilverTime.Text = TimeFormat.Format(challenge.SilverSeconds);
            BronzeTime.Text = TimeFormat.Format(challenge.BronzeSeconds);
            UpdateHeldMarks(challenge);
            StatusText.Text = "";
        }
        else
        {
            TitleText.Text = "No ARA challenge for this combination";
            Targets.Visibility = Visibility.Collapsed;
            // Conditions are shown too: a wet challenge simply won't match in the dry.
            StatusText.Text = $"track  {_sdk.TrackId}  {_sdk.TrackName}\n" +
                              $"car    {_sdk.CarId}  {_sdk.CarName}\n" +
                              $"cond   {(_sdk.IsWet ? "wet" : "dry")}";
        }
    }

    private void UpdateHeldMarks(Challenge challenge)
    {
        var held = _progress.Get(challenge.Number)?.BestMedal ?? Medal.None;

        GoldMark.Text = held >= Medal.Gold ? "✓" : "";
        SilverMark.Text = held >= Medal.Silver ? "✓" : "";
        BronzeMark.Text = held >= Medal.Bronze ? "✓" : "";
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
              $"{TimeFormat.FormatDelta(lap.Seconds - challenge.TargetFor(next))}"
            : $"last {TimeFormat.Format(lap.Seconds)}  gold held";

        if (earnedNewTier) ShowBanner(medal, lap.Seconds, challenge);
    }

    private void Quit()
    {
        // ShutdownMode is OnExplicitShutdown, so the window's OnClosed never runs — clean up here
        // or the tray icon lingers as a ghost until someone mouses over it.
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
        BannerSub.Text = $"#{challenge.Number}  {challenge.Name}";

        Banner.Visibility = Visibility.Visible;
        Panel.Visibility = Visibility.Collapsed;   // swap rather than stack, so nothing jumps

        _bannerTimer.Stop();
        _bannerTimer.Start();
        SystemSounds.Asterisk.Play();
    }

    private void HideBanner()
    {
        _bannerTimer.Stop();
        Banner.Visibility = Visibility.Collapsed;
        Panel.Visibility = Visibility.Visible;
    }
}
