using System.Reflection;
using System.Text.Json;

namespace AraOverlay.Core;

/// <summary>
/// The 20 ARA challenges, embedded in the exe as challenges.json and indexed for lookup by
/// track, car and condition.
/// </summary>
public sealed class ChallengeCatalog
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    private readonly Dictionary<(int Track, int Car, bool Wet), Challenge> _byCombination;

    public IReadOnlyList<Challenge> Challenges { get; }

    /// <summary>Validates and indexes the rows, once per track id.</summary>
    /// <param name="challenges">The challenge list.</param>
    /// <exception cref="InvalidDataException">A row is unusable, or two share a lookup key.</exception>
    private ChallengeCatalog(IReadOnlyList<Challenge> challenges)
    {
        Challenges = challenges;
        _byCombination = new Dictionary<(int, int, bool), Challenge>();

        foreach (var challenge in challenges)
        {
            challenge.Validate();
            foreach (var trackId in challenge.TrackIds)
            {
                var key = (trackId, challenge.CarId, challenge.Wet);
                if (!_byCombination.TryAdd(key, challenge))
                    throw new InvalidDataException(
                        $"Challenges {_byCombination[key].Number} and {challenge.Number} share the same " +
                        $"track+car+conditions ({trackId} / {challenge.CarId} / " +
                        $"{(challenge.Wet ? "wet" : "dry")}). The overlay could not tell them apart.");
            }
        }
    }

    public static ChallengeCatalog Embedded { get; } = LoadEmbedded();

    /// <summary>Builds a catalog from JSON text.</summary>
    /// <param name="json">A challenges.json document.</param>
    /// <returns>The catalog.</returns>
    /// <exception cref="InvalidDataException">The document isn't a usable list.</exception>
    public static ChallengeCatalog FromJson(string json)
    {
        var challenges = JsonSerializer.Deserialize<List<Challenge>>(json, JsonOptions)
            ?? throw new InvalidDataException("challenges.json did not contain a list.");
        return new ChallengeCatalog(challenges);
    }

    /// <summary>Matches a live session to a challenge.</summary>
    /// <param name="trackId">WeekendInfo:TrackID.</param>
    /// <param name="carId">The player's DriverInfo:Drivers:CarID.</param>
    /// <param name="wet">Whether the session counts as wet.</param>
    /// <returns>The matching challenge, or null.</returns>
    public Challenge? Find(int trackId, int carId, bool wet) =>
        _byCombination.GetValueOrDefault((trackId, carId, wet));

    /// <summary>Reads the challenges.json compiled into this assembly.</summary>
    /// <returns>The catalog it describes.</returns>
    /// <exception cref="InvalidDataException">The resource is missing or unusable.</exception>
    private static ChallengeCatalog LoadEmbedded()
    {
        var assembly = Assembly.GetExecutingAssembly();

        // Matched by suffix so renaming the root namespace can't silently break the lookup.
        var name = Array.Find(assembly.GetManifestResourceNames(), n => n.EndsWith("challenges.json", StringComparison.Ordinal))
            ?? throw new InvalidDataException("challenges.json is not embedded in the assembly.");

        using var stream = assembly.GetManifestResourceStream(name)!;
        using var reader = new StreamReader(stream);
        return FromJson(reader.ReadToEnd());
    }
}
