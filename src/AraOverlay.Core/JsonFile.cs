using System.Text.Json;

namespace AraOverlay.Core;

/// <summary>
/// The small JSON files kept under %APPDATA%. Neither is worth interrupting a session over, so a
/// file that can't be read gives back nothing and a failed write is dropped.
/// </summary>
public static class JsonFile
{
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };

    /// <summary>Builds a path inside the app's %APPDATA% folder.</summary>
    /// <param name="fileName">The file's name.</param>
    /// <returns>The full path; the folder may not exist yet.</returns>
    public static string PathIn(string fileName) => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "AraOverlay",
        fileName);

    /// <summary>Reads a JSON file.</summary>
    /// <param name="path">The file to read.</param>
    /// <typeparam name="T">What the document should deserialise to.</typeparam>
    /// <returns>The value, or default if the file is missing, corrupt or unreadable.</returns>
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

    /// <summary>Writes a JSON file, creating its folder if needed. Failures are swallowed.</summary>
    /// <param name="path">The file to write.</param>
    /// <param name="value">What to serialise.</param>
    /// <typeparam name="T">The value's type.</typeparam>
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
        }
    }
}
