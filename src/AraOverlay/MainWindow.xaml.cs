using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Media;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
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

    private const int WM_HOTKEY = 0x0312;
    private const int MOD_ALT = 0x0001;
    private const int MOD_CONTROL = 0x0002;
    private const int MOD_NOREPEAT = 0x4000;
    private const int VK_L = 0x4C;
    private const int LockHotkeyId = 1;

    [DllImport("user32.dll")] private static extern int GetWindowLong(IntPtr hWnd, int index);
    [DllImport("user32.dll")] private static extern int SetWindowLong(IntPtr hWnd, int index, int value);
    [DllImport("user32.dll")] private static extern bool RegisterHotKey(IntPtr hWnd, int id, int modifiers, int vk);
    [DllImport("user32.dll")] private static extern bool UnregisterHotKey(IntPtr hWnd, int id);

    private readonly Settings _settings = Settings.Load();
    private readonly ProgressStore _progress = new(JsonFile.PathIn("progress.json"));
    private readonly SdkService _sdk = new();
    private readonly DispatcherTimer _bannerTimer = new() { Interval = TimeSpan.FromSeconds(8) };
    private readonly DispatcherTimer _loginTimer = new() { Interval = TimeSpan.FromSeconds(1) };
    private readonly ApiClient _api = new();
    private readonly DispatcherTimer _refreshTimer = new() { Interval = TimeSpan.FromMinutes(15) };

    private Forms.NotifyIcon? _tray;
    private Forms.ToolStripMenuItem? _lockItem;
    private Forms.ToolStripMenuItem? _signInItem;
    private Forms.ToolStripMenuItem? _copyCodeItem;
    private Forms.ToolStripMenuItem? _matchItem;
    private CancellationTokenSource? _loginCancel;
    private LoginStatus _loginStatus = LoginStatus.Idle;
    private DateTimeOffset _loginSettled;
    private bool _refreshing;
    private IntPtr _hwnd;
    private bool _hotkeyClaimed;
    private double? _lastLap;
    private bool _demo;

    /// <summary>Restores the saved position and marshals the SDK's events onto the UI thread.</summary>
    public MainWindow()
    {
        InitializeComponent();

        Left = _settings.Left;
        Top = _settings.Top;

        _bannerTimer.Tick += (_, _) => HideBanner();
        _loginTimer.Tick += (_, _) => OnLoginTick();
        _refreshTimer.Tick += (_, _) => RefreshCatalog();

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
        ClaimLockHotkey();
        BuildTrayIcon();

        if (Environment.GetCommandLineArgs().Contains("--demo"))
        {
            StartDemo();
            return;
        }

        _sdk.SwapCatalog(CatalogCache.Resolve());
        _sdk.Start();

        _refreshTimer.Start();
        RefreshCatalog();
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

    /// <summary>
    /// Registers Ctrl+Alt+L system-wide, since the overlay never holds keyboard focus and never
    /// sees an input binding. Failure means another program already owns it; the tray still works.
    /// </summary>
    private void ClaimLockHotkey()
    {
        _hotkeyClaimed = RegisterHotKey(_hwnd, LockHotkeyId, MOD_CONTROL | MOD_ALT | MOD_NOREPEAT, VK_L);
        if (_hotkeyClaimed) HwndSource.FromHwnd(_hwnd)?.AddHook(OnWindowMessage);
    }

    /// <summary>Catches the lock hotkey.</summary>
    /// <param name="hwnd">Unused.</param>
    /// <param name="message">The window message.</param>
    /// <param name="wParam">The hotkey id, for WM_HOTKEY.</param>
    /// <param name="lParam">Unused.</param>
    /// <param name="handled">Set when the message was the lock hotkey.</param>
    /// <returns>Zero; the message is handled through the flag.</returns>
    private IntPtr OnWindowMessage(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (message != WM_HOTKEY || wParam.ToInt32() != LockHotkeyId) return IntPtr.Zero;

        ToggleLock();
        handled = true;
        return IntPtr.Zero;
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
            ShortcutKeyDisplayString = _hotkeyClaimed ? "Ctrl+Alt+L" : "",
        };

        _signInItem = new Forms.ToolStripMenuItem("Sign in…", null, (_, _) => ToggleSignIn());

        // The panel can't be clicked while locked, so the code is copied from here instead.
        _copyCodeItem = new Forms.ToolStripMenuItem("Copy code", null, (_, _) => CopyCode()) { Visible = false };

        var menu = new Forms.ContextMenuStrip();
        menu.Items.Add(_signInItem);
        menu.Items.Add(_copyCodeItem);
        menu.Items.Add(new Forms.ToolStripMenuItem("Check for updates now", null, (_, _) => RefreshCatalog()));

        // Shown only where more than one plan uses this track and car, which the sim cannot
        // resolve: both are real challenges and only the driver knows which they are running.
        _matchItem = new Forms.ToolStripMenuItem("Challenge") { Visible = false };
        menu.Items.Add(_matchItem);
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add(_lockItem);
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add(new Forms.ToolStripMenuItem("Exit", null, (_, _) => Quit()));

        UpdateSignInItem();

        _tray = new Forms.NotifyIcon
        {
            Icon = TrayIcon(),
            Text = "ARA Challenge Overlay",
            Visible = true,
            ContextMenuStrip = menu,
        };
    }

    /// <summary>How long a finished login's message stays up before the panel moves on.</summary>
    private static readonly TimeSpan SettledMessageFor = TimeSpan.FromSeconds(10);

    private static readonly Medal[] DemoTiers = [Medal.None, Medal.Bronze, Medal.Silver, Medal.Gold];

    /// <summary>Cycles every challenge and medal state with no sim attached, for --demo.</summary>
    private void StartDemo()
    {
        _demo = true;

        var challenges = ChallengeCatalog.Embedded.Challenges.OrderBy(c => c.Number).ToList();

        // Opens on the waiting art, then an unmatched session, then the challenge cycle.
        var step = -1;

        var demo = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2.5) };
        demo.Tick += (_, _) =>
        {
            // The cycle would otherwise redraw the panel over a login every 2.5 seconds, leaving
            // the code flashing between challenge tiles for as long as it took to read it.
            if (SigningIn || _loginStatus.Message.Length > 0) return;

            if (step < 0)
            {
                step++;
                HideBanner();
                ShowUnmatched("track  0  \ncar    0  \ncond   dry");
                return;
            }

            var index = step / 2 % challenges.Count;
            var challenge = challenges[index];
            var medal = DemoTiers[index % DemoTiers.Length];
            var lap = medal == Medal.None ? challenge.BronzeSeconds + 1.234 : challenge.TargetFor(medal) - 0.05;

            if (step % 2 == 0)
            {
                HideBanner();

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

        ShowLayer(Layer.Waiting);
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

        // Outranks the sim: signing in almost always happens before iRacing is running, and a
        // login the member can't see is a login they think did nothing.
        if (SigningIn || _loginStatus.Message.Length > 0)
        {
            ShowLogin();
            return;
        }

        if (!_sdk.Connected) return;

        UpdateMatchItem();

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

    /// <summary>Whether a login is open, which the panel outranks the sim state for.</summary>
    private bool SigningIn => _loginStatus.State is LoginState.Starting or LoginState.WaitingForBrowser;

    /// <summary>Starts a login, or cancels the one already running.</summary>
    private void ToggleSignIn()
    {
        if (SigningIn) { CancelLogin(); UpdateSignInItem(); RenderState(); return; }
        if (TokenStore.Load() is { Usable: true }) { SignOut(); return; }

        SignIn();
    }

    /// <summary>
    /// Runs a login in the background. The controller raises its progress on whatever thread the
    /// HTTP call finished on, so every report is marshalled back before it touches the panel.
    /// </summary>
    private void SignIn()
    {
        CancelLogin();
        _loginCancel = new CancellationTokenSource();

        var login = new LoginController(_api, OpenBrowser);
        login.Changed += status => Dispatcher.InvokeAsync(() => OnLoginChanged(status));

        var cancel = _loginCancel.Token;

        _ = Task.Run(async () =>
        {
            var result = await login.Run(cancel).ConfigureAwait(false);
            if (result is { } session) TokenStore.Save(new StoredSession(session.AccessToken, session.Expires));

            await Dispatcher.InvokeAsync(() =>
            {
                UpdateSignInItem();
                if (result is not null) RefreshCatalog();
            });
        }, cancel);
    }

    /// <summary>
    /// Fetches the catalog in the background and stages it into the SDK. A failed refresh keeps
    /// whatever is already loaded: the contract is that an upstream problem must never reach the
    /// panel as a shorter list of challenges, and a driver mid-session would read that as one
    /// being retired.
    /// </summary>
    private void RefreshCatalog()
    {
        if (_demo || _refreshing) return;
        if (TokenStore.Load() is not { Usable: true } stored) return;

        _refreshing = true;

        _ = Task.Run(async () =>
        {
            CatalogFetch fetched;

            try
            {
                fetched = await CatalogFetcher.Fetch(_api, stored.Token).ConfigureAwait(false);
            }
            catch (Exception e)
            {
                // Anything the fetcher didn't expect. Losing the flag here would stop every later
                // refresh for the rest of the session, silently.
                await Dispatcher.InvokeAsync(() => { _refreshing = false; SdkService.Log($"{e.GetType().Name}: {e.Message}"); });
                return;
            }

            await Dispatcher.InvokeAsync(() =>
            {
                _refreshing = false;

                if (fetched.Skipped.Count > 0) SdkService.Log(string.Join(Environment.NewLine, fetched.Skipped));

                if (!fetched.Usable)
                {
                    if (fetched.Error is { } error) SdkService.Log(error);
                    return;
                }

                try
                {
                    // Built before it is cached: a catalog the lookup rejects must not replace a
                    // good file on disk.
                    var catalog = new ChallengeCatalog(fetched.Challenges);
                    if (catalog.Skipped.Count > 0) SdkService.Log(string.Join(Environment.NewLine, catalog.Skipped));

                    CatalogCache.Save(fetched.Challenges);
                    _sdk.SwapCatalog(catalog);
                }
                catch (InvalidDataException e)
                {
                    // Two challenges the overlay could not tell apart. Keeping the old catalog is
                    // the only safe answer; the log is how this gets reported to ARA.
                    SdkService.Log(e.Message);
                }
            });
        });
    }

    /// <summary>Revokes the session with the server, then forgets it locally either way.</summary>
    private void SignOut()
    {
        if (TokenStore.Load() is { } stored)
            _ = Task.Run(() => _api.RevokeSession(stored.Token));

        TokenStore.Clear();
        UpdateSignInItem();
    }

    /// <summary>Stops a login in progress and clears the panel.</summary>
    private void CancelLogin()
    {
        _loginCancel?.Cancel();
        _loginCancel?.Dispose();
        _loginCancel = null;
        _loginStatus = LoginStatus.Idle;
        _loginTimer.Stop();
    }

    /// <summary>Opens the Academy's verification page in the member's own browser.</summary>
    /// <param name="uri">The https URI the API returned.</param>
    private void OpenBrowser(string uri)
    {
        try
        {
            Process.Start(new ProcessStartInfo(uri) { UseShellExecute = true });
        }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            // No default browser. The code is still on the panel and the URI is on the page.
        }
    }

    /// <summary>Takes a progress report from the login and redraws.</summary>
    /// <param name="status">Where the login has got to.</param>
    private void OnLoginChanged(LoginStatus status)
    {
        _loginStatus = status;

        // A finished login only lingers if it has something to say; a successful one says nothing
        // and the panel goes straight back to the challenge.
        _loginSettled = status.State == LoginState.Failed ? DateTimeOffset.UtcNow : default;

        _loginTimer.Stop();
        if (SigningIn || status.Message.Length > 0) _loginTimer.Start();

        if (_copyCodeItem is not null) _copyCodeItem.Visible = status.UserCode.Length > 0;

        UpdateSignInItem();
        RenderState();
    }

    /// <summary>Puts the code on the clipboard, for a panel that can't be clicked.</summary>
    private void CopyCode()
    {
        if (_loginStatus.UserCode.Length == 0) return;

        try
        {
            System.Windows.Clipboard.SetText(_loginStatus.UserCode);
        }
        catch (COMException)
        {
            // Another process has the clipboard open. The code is still readable on the panel.
        }
    }

    /// <summary>Relabels the tray item for what clicking it would now do.</summary>
    private void UpdateSignInItem()
    {
        if (_signInItem is null) return;

        _signInItem.Text = SigningIn ? "Cancel sign-in"
            : TokenStore.Load() is { Usable: true } ? "Sign out"
            : "Sign in…";
    }

    /// <summary>
    /// Runs the login panel's clock: the countdown while waiting, then clearing a finished
    /// login so the panel goes back to the challenge rather than sitting on the message.
    /// </summary>
    private void OnLoginTick()
    {
        if (_loginSettled != default && DateTimeOffset.UtcNow - _loginSettled > SettledMessageFor)
        {
            _loginStatus = LoginStatus.Idle;
            _loginSettled = default;
            _loginTimer.Stop();
            RenderState();
            return;
        }

        ShowLogin();
    }

    /// <summary>Fills the login rows, including the countdown, which ticks once a second.</summary>
    private void ShowLogin()
    {
        TitleText.Text = "ARA CHALLENGE OVERLAY";
        SetPanelMode(PanelMode.SigningIn);

        // Spaced, because it is being retyped by hand into a browser on the same screen.
        LoginCode.Text = _loginStatus.UserCode.Length == 8
            ? _loginStatus.UserCode.Insert(4, " ")
            : _loginStatus.UserCode;

        LoginPrompt.Visibility = _loginStatus.UserCode.Length > 0 ? Visibility.Visible : Visibility.Collapsed;

        var left = _loginStatus.Expires - DateTimeOffset.UtcNow;

        LoginMessage.Text = _loginStatus.State == LoginState.WaitingForBrowser && left > TimeSpan.Zero
            ? $"{_loginStatus.Message}  ·  {left:m\\:ss} left"
            : _loginStatus.Message;
    }

    private enum PanelMode { Challenge, Unmatched, SigningIn }

    /// <summary>Swaps between the challenge rows, the id listing, and the login code.</summary>
    /// <param name="mode">Which of the three the panel is showing.</param>
    private void SetPanelMode(PanelMode mode)
    {
        var rows = mode == PanelMode.Challenge ? Visibility.Visible : Visibility.Collapsed;

        TrackText.Visibility = CarText.Visibility = rows;
        HeaderDivider1.Visibility = HeaderDivider2.Visibility = rows;
        GoalRow.Visibility = DeltaRow.Visibility = rows;
        LapRow.Visibility = Targets.Visibility = rows;
        StatusText.Visibility = mode == PanelMode.Unmatched ? Visibility.Visible : Visibility.Collapsed;
        Login.Visibility = mode == PanelMode.SigningIn ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>Shows the ids the sim reported, for a track and car matching no challenge.</summary>
    /// <param name="detail">The lines to print under the title.</param>
    private void ShowUnmatched(string detail)
    {
        TitleText.Text = "NO ARA CHALLENGE FOR THIS COMBINATION";
        SetPanelMode(PanelMode.Unmatched);
        StatusText.Text = detail;
    }

    /// <summary>Fills every row of the panel for a challenge.</summary>
    /// <param name="challenge">The challenge to show.</param>
    /// <param name="held">Which medals to treat as held; defaults to the stored progress.</param>
    private void ShowChallenge(Challenge challenge, Medal? held = null)
    {
        TitleText.Text = challenge.Plan.Length > 0
            ? $"{challenge.Plan.ToUpperInvariant()}  ·  {challenge.Number}{(_sdk.IsWet ? "  ·  WET" : "")}"
            : $"CHALLENGE {challenge.Number}{(_sdk.IsWet ? "  ·  WET" : "")}";
        TrackText.Text = challenge.Track.ToUpperInvariant();
        CarText.Text = challenge.Car.ToUpperInvariant();

        SetPanelMode(PanelMode.Challenge);

        GoldTime.Text = TimeFormat.Format(challenge.GoldSeconds);
        SilverTime.Text = TimeFormat.Format(challenge.SilverSeconds);
        BronzeTime.Text = TimeFormat.Format(challenge.BronzeSeconds);

        UpdateLiveRows(challenge, held ?? HeldMedal(challenge));
    }

    /// <summary>
    /// Rebuilds the tray's challenge picker. It appears only when this track and car belong to
    /// more than one plan, which is the one case the overlay cannot decide on its own.
    /// </summary>
    private void UpdateMatchItem()
    {
        if (_matchItem is null) return;

        _matchItem.Visible = _sdk.Matches.Count > 1;
        _matchItem.DropDownItems.Clear();

        if (!_matchItem.Visible) return;

        for (var i = 0; i < _sdk.Matches.Count; i++)
        {
            var match = _sdk.Matches[i];
            var index = i;

            _matchItem.DropDownItems.Add(new Forms.ToolStripMenuItem(
                match.Plan.Length > 0 ? $"{match.Plan} — {match.Number}" : $"Challenge {match.Number}",
                null,
                (_, _) => _sdk.SelectMatch(index))
            {
                Checked = ReferenceEquals(match, _sdk.Challenge),
            });
        }
    }

    /// <summary>Reads the best medal stored against a challenge.</summary>
    /// <param name="challenge">The challenge to look up.</param>
    /// <returns>The stored medal, or None.</returns>
    private Medal HeldMedal(Challenge challenge) => _progress.Get(challenge.Key)?.BestMedal ?? Medal.None;

    /// <summary>
    /// Refills the rows that move as laps come in: the held ticks, the goal tier, the big delta
    /// from the last lap to that tier's target, and the two lap times. Once gold is held the goal
    /// stays gold, so the delta keeps meaning something.
    /// </summary>
    /// <param name="challenge">The challenge being shown.</param>
    /// <param name="medal">The medal to treat as held.</param>
    private void UpdateLiveRows(Challenge challenge, Medal medal)
    {
        GoldMark.Text = medal >= Medal.Gold ? "✓" : "";
        SilverMark.Text = medal >= Medal.Silver ? "✓" : "";
        BronzeMark.Text = medal >= Medal.Bronze ? "✓" : "";

        var goal = Challenge.NextTierAbove(medal) ?? Medal.Gold;
        var target = challenge.TargetFor(goal);

        GoalTier.Text = goal.ToString().ToUpperInvariant();
        GoalTier.Foreground = (SolidColorBrush)FindResource(goal.ToString());
        GoalTime.Text = TimeFormat.Format(target);

        SessionBestText.Text = _sdk.SessionBest is { } best ? TimeFormat.Format(best) : "—";

        if (_lastLap is not { } lap)
        {
            LastLapText.Text = "—";
            DeltaText.Text = "—";
            DeltaText.Foreground = (SolidColorBrush)FindResource("Dim");
            return;
        }

        LastLapText.Text = TimeFormat.Format(lap);

        var delta = lap - target;
        var behind = delta > 0;

        DeltaText.Text = delta.ToString("+0.000;-0.000", CultureInfo.InvariantCulture) + (behind ? "s ▼" : "s ▲");
        DeltaText.Foreground = (SolidColorBrush)FindResource(behind ? "Behind" : "Ahead");
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
        var earnedNewTier = _progress.RecordLap(challenge.Key, lap.Seconds, medal);

        UpdateLiveRows(challenge, HeldMedal(challenge));

        if (earnedNewTier) ShowBanner(medal, lap.Seconds, challenge);
    }

    /// <summary>Tears down the tray icon and the SDK, then exits. The only shutdown path.</summary>
    private void Quit()
    {
        if (_hotkeyClaimed) UnregisterHotKey(_hwnd, LockHotkeyId);
        if (_tray is not null) { _tray.Visible = false; _tray.Dispose(); _tray = null; }
        CancelLogin();
        _refreshTimer.Stop();
        _api.Dispose();
        _sdk.Dispose();
        Application.Current.Shutdown();
    }

    private enum Layer { Waiting, Panel, Medal }

    /// <summary>Shows one layer and hides the other two.</summary>
    /// <param name="layer">The layer to show.</param>
    private void ShowLayer(Layer layer)
    {
        Waiting.Visibility = layer == Layer.Waiting ? Visibility.Visible : Visibility.Collapsed;
        Panel.Visibility = layer == Layer.Panel ? Visibility.Visible : Visibility.Collapsed;
        Banner.Visibility = layer == Layer.Medal ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>What shows when no medal banner is up: the panel, or the waiting art.</summary>
    private Layer RestingLayer =>
        _demo || _sdk.Connected || SigningIn || _loginStatus.Message.Length > 0 ? Layer.Panel : Layer.Waiting;

    /// <summary>Shows the medal banner in place of the panel, for eight seconds.</summary>
    /// <param name="medal">The medal earned; None shows nothing.</param>
    /// <param name="seconds">The lap time that earned it.</param>
    /// <param name="challenge">The challenge it was set on.</param>
    private void ShowBanner(Medal medal, double seconds, Challenge challenge)
    {
        if (medal == Medal.None) return;

        BannerTitle.Text = medal.ToString().ToUpperInvariant();
        BannerArt.Background = (ImageBrush)FindResource($"{medal}Art");
        Banner.BorderBrush = (SolidColorBrush)FindResource(medal.ToString());
        BannerTime.Text = TimeFormat.Format(seconds);
        BannerSub.Text = $"Challenge {challenge.Number} — {challenge.Track}";

        ShowLayer(Layer.Medal);

        _bannerTimer.Stop();
        _bannerTimer.Start();
        if (!_demo) SystemSounds.Asterisk.Play();
    }

    /// <summary>Drops back to the resting layer. Safe when no banner is showing.</summary>
    private void HideBanner()
    {
        _bannerTimer.Stop();
        ShowLayer(RestingLayer);
    }
}
