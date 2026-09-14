using System.Threading;
using System.Windows;

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

    /// <summary>Claims the instance mutex and shows the overlay, or bows out if one is running.</summary>
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
        new MainWindow().Show();
    }
}
