using AraOverlay.Core;

namespace AraOverlay;

/// <summary>Where the driver dragged the overlay to, and whether it's pinned there.</summary>
public sealed class Settings
{
    public double Left { get; set; } = 40;
    public double Top { get; set; } = 40;
    public bool Locked { get; set; } = true;

    private static string FilePath => JsonFile.PathIn("settings.json");

    /// <summary>Reads the saved settings.</summary>
    /// <returns>The stored values, or defaults if there's no usable file.</returns>
    public static Settings Load() => JsonFile.Load<Settings>(FilePath) ?? new Settings();

    /// <summary>Writes the current values.</summary>
    public void Save() => JsonFile.Save(FilePath, this);
}
