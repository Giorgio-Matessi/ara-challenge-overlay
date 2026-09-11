using System.Threading;
using System.Windows;

// UseWindowsForms adds System.Windows.Forms to the implicit usings, and it has its own
// Application and MessageBox. This file wants the WPF ones.
using Application = System.Windows.Application;
using MessageBox = System.Windows.MessageBox;

namespace AraOverlay;

public partial class App : Application
{
    /// <summary>
    /// Held for the life of the process. A second copy fails to acquire it and bows out, because
    /// two overlays would stack on screen and the second to write would clobber progress.json.
    /// </summary>
    private static Mutex? _singleInstance;

    protected override void OnStartup(StartupEventArgs e)
    {
        // Session-local, so fast user switching still gives each signed-in user their own.
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

        // Created here rather than by StartupUri, so nothing is constructed before the check above.
        new MainWindow().Show();
    }
}
