using AraOverlay.Core;

namespace AraOverlay;

/// <summary>Where the driver dragged the overlay to, and whether it's pinned there.</summary>
public sealed class Settings
{
    public double Left { get; set; } = 40;
    public double Top { get; set; } = 40;

    /// <summary>Locked means click-through: the mouse passes straight to iRacing.</summary>
    public bool Locked { get; set; } = true;

    private static string FilePath => JsonFile.PathIn("settings.json");

    public static Settings Load() => JsonFile.Load<Settings>(FilePath) ?? new Settings();

    public void Save() => JsonFile.Save(FilePath, this);
}
