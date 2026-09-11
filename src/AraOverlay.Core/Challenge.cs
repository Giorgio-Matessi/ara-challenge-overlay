using System.Text.Json.Serialization;

namespace AraOverlay.Core;

/// <summary>Ordered so that a higher tier compares greater, and so that +1 is the tier above.</summary>
public enum Medal
{
    None = 0,
    Bronze = 1,
    Silver = 2,
    Gold = 3,
}

/// <summary>
/// One ARA challenge: a fixed track + car + conditions, with three lap-time targets.
/// Times are authored as strings ("1:23.456") so challenges.json stays hand-editable.
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

    /// <summary>iRacing's WeekendInfo:TrackID — the layout, not the track package.</summary>
    public int TrackId { get; init; }

    /// <summary>iRacing's DriverInfo:Drivers:CarID.</summary>
    public int CarId { get; init; }

    /// <summary>
    /// True for the wet-weather challenges. Track and car alone don't identify a challenge:
    /// #14 and #19 are the same car on the same Le Mans layout, dry and wet.
    /// </summary>
    public bool Wet { get; init; }

    public string Gold { get; init; } = "";
    public string Silver { get; init; } = "";
    public string Bronze { get; init; } = "";

    [JsonIgnore] public double GoldSeconds => TimeFormat.Parse(Gold);
    [JsonIgnore] public double SilverSeconds => TimeFormat.Parse(Silver);
    [JsonIgnore] public double BronzeSeconds => TimeFormat.Parse(Bronze);

    /// <summary>Tiers fastest first, which is also the order a lap is tested against them.</summary>
    private (Medal Medal, double Seconds)[] Tiers =>
        [(Medal.Gold, GoldSeconds), (Medal.Silver, SilverSeconds), (Medal.Bronze, BronzeSeconds)];

    /// <summary>The best medal this lap time earns, or <see cref="Medal.None"/>.</summary>
    public Medal MedalFor(double lapSeconds)
    {
        if (lapSeconds <= 0) return Medal.None;   // -1 is the SDK's "no lap yet"

        foreach (var (medal, seconds) in Tiers)
            if (lapSeconds <= seconds + DisplayTolerance) return medal;

        return Medal.None;
    }

    /// <summary>Threshold in seconds for a tier. <see cref="Medal.None"/> has no threshold.</summary>
    public double TargetFor(Medal medal)
    {
        foreach (var tier in Tiers)
            if (tier.Medal == medal) return tier.Seconds;

        throw new ArgumentOutOfRangeException(nameof(medal), medal, "No threshold for this tier.");
    }

    /// <summary>The tier a driver is chasing next, or null once they hold gold.</summary>
    public static Medal? NextTierAbove(Medal held) => held == Medal.Gold ? null : held + 1;

    /// <summary>Throws if the three times aren't strictly ordered — catches a typo at load time.</summary>
    public void Validate()
    {
        if (!(GoldSeconds < SilverSeconds && SilverSeconds < BronzeSeconds))
            throw new InvalidDataException(
                $"Challenge {Number} ('{Name}') has times out of order: " +
                $"gold {Gold}, silver {Silver}, bronze {Bronze}.");
    }
}
