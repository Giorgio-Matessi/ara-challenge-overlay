namespace AraOverlay.Core;

/// <summary>iRacing's irsdk_TrackWetness values, as read from the TrackWetness telemetry var.</summary>
public static class TrackWetness
{
    public const int Unknown = 0;
    public const int Dry = 1;
    public const int MostlyDry = 2;
    public const int VeryLightlyWet = 3;
    public const int LightlyWet = 4;
    public const int ModeratelyWet = 5;
    public const int VeryWet = 6;
    public const int ExtremelyWet = 7;
}

/// <summary>
/// Decides whether a session counts as wet, which is part of what identifies a challenge. The
/// two thresholds differ so a drying track can't flip the active challenge back and forth.
/// </summary>
public sealed class ConditionTracker
{
    private const int BecomesWetAt = TrackWetness.LightlyWet;
    private const int BecomesDryAt = TrackWetness.MostlyDry;

    public bool IsWet { get; private set; }

    /// <summary>Returns to dry, for a new session or a lost connection.</summary>
    public void Reset() => IsWet = false;

    /// <summary>Feeds one telemetry frame.</summary>
    /// <param name="trackWetness">The TrackWetness reading.</param>
    /// <param name="declaredWet">WeatherDeclaredWet, which forces wet on its own.</param>
    /// <returns>True on the frame the classification flips.</returns>
    public bool Update(int trackWetness, bool declaredWet)
    {
        if (trackWetness == TrackWetness.Unknown && !declaredWet) return false;

        var wet = IsWet
            ? declaredWet || trackWetness > BecomesDryAt
            : declaredWet || trackWetness >= BecomesWetAt;

        if (wet == IsWet) return false;

        IsWet = wet;
        return true;
    }
}
