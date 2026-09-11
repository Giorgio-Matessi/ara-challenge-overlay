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
/// Decides whether the session counts as wet, since challenges 16-20 are the wet versions of a
/// track+car pair and #14/#19 are the same car on the same layout in different conditions.
///
/// The two thresholds are deliberately different: a track drying out passes through the middle
/// of the range, and a single threshold there would flip the active challenge back and forth.
/// </summary>
public sealed class ConditionTracker
{
    private const int BecomesWetAt = TrackWetness.LightlyWet;
    private const int BecomesDryAt = TrackWetness.MostlyDry;

    public bool IsWet { get; private set; }

    public void Reset() => IsWet = false;

    /// <summary>Feeds one frame. Returns true on the frame the classification actually flips.</summary>
    public bool Update(int trackWetness, bool declaredWet)
    {
        if (trackWetness == TrackWetness.Unknown && !declaredWet) return false;   // no reading yet

        var wet = IsWet
            ? declaredWet || trackWetness > BecomesDryAt
            : declaredWet || trackWetness >= BecomesWetAt;

        if (wet == IsWet) return false;

        IsWet = wet;
        return true;
    }
}
