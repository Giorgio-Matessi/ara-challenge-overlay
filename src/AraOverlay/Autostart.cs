using Microsoft.Win32;

namespace AraOverlay;

/// <summary>
/// Start with Windows, as a value under the user's Run key. The registry is the only record, so
/// the tray tick stays honest if the member removes it from Task Manager's Startup tab.
/// </summary>
public static class Autostart
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "ARA Challenge Overlay";

    /// <summary>Reads or sets whether Windows launches this exe at sign-in.</summary>
    public static bool Enabled
    {
        get
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey);
            return key?.GetValue(ValueName) is not null;
        }
        set
        {
            using var key = Registry.CurrentUser.CreateSubKey(RunKey);
            if (value) key.SetValue(ValueName, $"\"{Environment.ProcessPath}\"");
            else key.DeleteValue(ValueName, throwOnMissingValue: false);
        }
    }
}
