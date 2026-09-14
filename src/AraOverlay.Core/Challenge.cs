using System.Text.Json.Serialization;

namespace AraOverlay.Core;

public enum Medal
{
    None = 0,
    Bronze = 1,
    Silver = 2,
    Gold = 3,
}

/// <summary>
/// One ARA challenge: a fixed track, car and condition with three lap-time targets.
/// </summary>
public sealed class Challenge
{
    private const double DisplayTolerance = 0.0005;

    public int Number { get; init; }
    public string Track { get; init; } = "";
    public string Car { get; init; } = "";
    public int[] TrackIds { get; init; } = [];
    public int CarId { get; init; }
    public bool Wet { get; init; }

    public string Gold { get; init; } = "";
    public string Silver { get; init; } = "";
    public string Bronze { get; init; } = "";

    [JsonIgnore] public double GoldSeconds => TimeFormat.Parse(Gold);
    [JsonIgnore] public double SilverSeconds => TimeFormat.Parse(Silver);
    [JsonIgnore] public double BronzeSeconds => TimeFormat.Parse(Bronze);

    private (Medal Medal, double Seconds)[] Tiers =>
        [(Medal.Gold, GoldSeconds), (Medal.Silver, SilverSeconds), (Medal.Bronze, BronzeSeconds)];

    /// <summary>Grades a lap against the three targets.</summary>
    /// <param name="lapSeconds">The lap time; the SDK reports -1 when there isn't one.</param>
    /// <returns>The best medal the lap earns, or None.</returns>
    public Medal MedalFor(double lapSeconds)
    {
        if (lapSeconds <= 0) return Medal.None;

        foreach (var (medal, seconds) in Tiers)
            if (lapSeconds <= seconds + DisplayTolerance) return medal;

        return Medal.None;
    }

    /// <summary>Looks up one tier's target.</summary>
    /// <param name="medal">Gold, Silver or Bronze.</param>
    /// <returns>The threshold in seconds.</returns>
    public double TargetFor(Medal medal) => medal switch
    {
        Medal.Gold => GoldSeconds,
        Medal.Silver => SilverSeconds,
        Medal.Bronze => BronzeSeconds,
        _ => throw new ArgumentOutOfRangeException(nameof(medal), medal, "No threshold for this tier."),
    };

    /// <summary>Works out what a driver is aiming at next.</summary>
    /// <param name="held">The best medal they hold.</param>
    /// <returns>The tier above it, or null once they hold gold.</returns>
    public static Medal? NextTierAbove(Medal held) => held == Medal.Gold ? null : held + 1;

    /// <summary>Checks one row is usable, so a typo fails at load rather than mid-session.</summary>
    /// <exception cref="InvalidDataException">No track ids, or times out of order.</exception>
    public void Validate()
    {
        if (TrackIds.Length == 0)
            throw new InvalidDataException($"Challenge {Number} ('{Track}') has no trackIds.");

        if (!(GoldSeconds < SilverSeconds && SilverSeconds < BronzeSeconds))
            throw new InvalidDataException(
                $"Challenge {Number} ('{Track}') has times out of order: " +
                $"gold {Gold}, silver {Silver}, bronze {Bronze}.");
    }
}
