using System.Text.Json;

namespace AraOverlay.Core;

/// <summary>
/// The small JSON files kept under %APPDATA% — medal progress and window position. Neither is
/// worth interrupting a session over, so a missing, corrupt or unreadable file reads as "no
/// value" and a failed write is dropped.
/// </summary>
public static class JsonFile
{
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };

    public static string PathIn(string fileName) => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "AraOverlay",
        fileName);

    public static T? Load<T>(string path)
    {
        try
        {
            return File.Exists(path) ? JsonSerializer.Deserialize<T>(File.ReadAllText(path)) : default;
        }
        catch (Exception e) when (e is JsonException or IOException or UnauthorizedAccessException)
        {
            return default;
        }
    }

    public static void Save<T>(string path, T value)
    {
        try
        {
            var directory = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
            File.WriteAllText(path, JsonSerializer.Serialize(value, Options));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // Losing a window position or one lap's progress beats crashing mid-session.
        }
    }
}
