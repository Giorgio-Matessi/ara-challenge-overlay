using System.Text.Json.Serialization;

namespace AraOverlay.Core;

/// <summary>
/// One ARA challenge: a fixed track + car, with three lap-time targets.
/// Times are authored as strings ("1:23.456") and parsed on first use.
/// </summary>
public sealed class Challenge
{
    /// <summary>
    /// The league rule is "quicker than the listed time", but iRacing hands us a float and the
    /// sim shows three decimals: a lap of 53.5004 displays as "53.500" and would look like a
    /// pass. So a lap counts if it is at most half a millisecond over. Set this to 0 to make
    /// the comparison strictly faster-than.
    /// </summary>
    private const double DisplayTolerance = 0.0005;

    public int Number { get; init; }
    public string Name { get; init; } = "";

    /// <summary>iRacing's WeekendInfo:TrackName, e.g. "limerock full" — not the display name.</summary>
    public string TrackId { get; init; } = "";

    /// <summary>iRacing's DriverInfo:Drivers:CarPath, e.g. "mx5 mx52016" — not the display name.</summary>
    public string CarId { get; init; } = "";

    /// <summary>
    /// True for the wet-weather challenges. Track and car alone don't identify a challenge:
    /// #14 and #19 are the same car on the same Le Mans layout, dry and wet.
    /// </summary>
    public bool Wet { get; init; }

    public string Gold { get; init; } = "";
    public string Silver { get; init; } = "";
    public string Bronze { get; init; } = "";

    private double? _gold, _silver, _bronze;

    [JsonIgnore] public double GoldSeconds => _gold ??= TimeFormat.Parse(Gold);
    [JsonIgnore] public double SilverSeconds => _silver ??= TimeFormat.Parse(Silver);
    [JsonIgnore] public double BronzeSeconds => _bronze ??= TimeFormat.Parse(Bronze);

    /// <summary>The best medal this lap time earns, or <see cref="Medal.None"/>.</summary>
    public Medal MedalFor(double lapSeconds)
    {
        if (lapSeconds <= 0) return Medal.None;   // -1 is the SDK's "no lap yet"
        if (Beats(lapSeconds, GoldSeconds)) return Medal.Gold;
        if (Beats(lapSeconds, SilverSeconds)) return Medal.Silver;
        if (Beats(lapSeconds, BronzeSeconds)) return Medal.Bronze;
        return Medal.None;
    }

    /// <summary>Threshold in seconds for a tier. <see cref="Medal.None"/> has no threshold.</summary>
    public double TargetFor(Medal medal) => medal switch
    {
        Medal.Gold => GoldSeconds,
        Medal.Silver => SilverSeconds,
        Medal.Bronze => BronzeSeconds,
        _ => throw new ArgumentOutOfRangeException(nameof(medal), medal, "No threshold for this tier."),
    };

    /// <summary>The tier a driver is chasing next, or null once they hold gold.</summary>
    public static Medal? NextTierAbove(Medal held) => held switch
    {
        Medal.None => Medal.Bronze,
        Medal.Bronze => Medal.Silver,
        Medal.Silver => Medal.Gold,
        _ => null,
    };

    /// <summary>Throws if the three times aren't strictly ordered — catches a typo at load time.</summary>
    public void Validate()
    {
        if (!(GoldSeconds < SilverSeconds && SilverSeconds < BronzeSeconds))
            throw new InvalidDataException(
                $"Challenge {Number} ('{Name}') has times out of order: " +
                $"gold {Gold}, silver {Silver}, bronze {Bronze}.");
    }

    private static bool Beats(double lap, double target) => lap <= target + DisplayTolerance;
}
