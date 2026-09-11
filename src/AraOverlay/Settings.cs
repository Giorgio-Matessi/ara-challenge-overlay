using System.IO;
using System.Text.Json;

namespace AraOverlay;

/// <summary>Where the driver dragged the overlay to, and whether it's pinned there.</summary>
public sealed class Settings
{
    public double Left { get; set; } = 40;
    public double Top { get; set; } = 40;

    /// <summary>Locked means click-through: the mouse passes straight to iRacing.</summary>
    public bool Locked { get; set; } = true;

    private static string Path_ => System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "AraOverlay",
        "settings.json");

    public static Settings Load()
    {
        try
        {
            return File.Exists(Path_)
                ? JsonSerializer.Deserialize<Settings>(File.ReadAllText(Path_)) ?? new Settings()
                : new Settings();
        }
        catch (Exception e) when (e is JsonException or IOException or UnauthorizedAccessException)
        {
            return new Settings();
        }
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path_)!);
            File.WriteAllText(Path_, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // Not worth interrupting a session over.
        }
    }
}
