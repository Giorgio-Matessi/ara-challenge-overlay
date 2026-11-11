using System.Threading;
using System.Windows;
using System.Windows.Interop;

using Application = System.Windows.Application;
using MessageBox = System.Windows.MessageBox;

namespace AraOverlay;

/// <summary>
/// Application entry point. Enforces a single instance, since two overlays would stack on screen
/// and the second to write would clobber progress.json.
/// </summary>
public partial class App : Application
{
    private static Mutex? _singleInstance;

    /// <summary>
    /// Claims the instance mutex and creates the overlay, or bows out if one is running. Only the
    /// handle is created here; the overlay shows itself, unless it was asked to start minimized.
    /// </summary>
    /// <param name="e">Startup arguments.</param>
    protected override void OnStartup(StartupEventArgs e)
    {
        _singleInstance = new Mutex(initiallyOwned: true, @"Local\AraChallengeOverlay", out var isFirst);

        if (!isFirst)
        {
            MessageBox.Show(
                "ARA Challenge Overlay is already running — look for it in the system tray.",
                "Already running",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            Shutdown();
            return;
        }

        base.OnStartup(e);
        new WindowInteropHelper(new MainWindow()).EnsureHandle();
    }
}
