using System.Reflection;
using System.Text.Json;

namespace AraOverlay.Core;

/// <summary>
/// The 20 ARA challenges, embedded in the exe as challenges.json and indexed for lookup by
/// track and car.
///
/// Wet is deliberately not part of the key. The ARA Labs API carries no weather field and cannot
/// gain one, so a challenge's conditions have to be read off its targets: exactly one track and
/// car combination is used twice — Le Mans, challenges 14 and 19 — and the wet one is 34 seconds
/// slower. Everywhere else a track and car identify a challenge on their own, and the league
/// session sets the weather anyway.
/// </summary>
public sealed class ChallengeCatalog
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    private readonly Dictionary<(int Track, int Car), List<Challenge>> _byCombination;

    public IReadOnlyList<Challenge> Challenges { get; }

    /// <summary>Validates and indexes the rows, once per track id.</summary>
    /// <param name="challenges">The challenge list.</param>
    /// <exception cref="InvalidDataException">A row is unusable, or a combination is ambiguous.</exception>
    private ChallengeCatalog(IReadOnlyList<Challenge> challenges)
    {
        Challenges = challenges;
        _byCombination = new Dictionary<(int, int), List<Challenge>>();

        foreach (var challenge in challenges)
        {
            challenge.Validate();
            foreach (var trackId in challenge.TrackIds)
            {
                if (!_byCombination.TryGetValue((trackId, challenge.CarId), out var sharing))
                    _byCombination[(trackId, challenge.CarId)] = sharing = [];
                sharing.Add(challenge);
            }
        }

        foreach (var (key, sharing) in _byCombination) Ambiguous(key, sharing);
    }

    /// <summary>Rejects a combination the overlay could not resolve from a live session.</summary>
    /// <param name="key">The track and car they share.</param>
    /// <param name="sharing">The challenges filed under it.</param>
    /// <exception cref="InvalidDataException">More than two, or two with the same targets.</exception>
    private static void Ambiguous((int Track, int Car) key, List<Challenge> sharing)
    {
        var numbers = string.Join(" and ", sharing.Select(c => c.Number));

        if (sharing.Count > 2)
            throw new InvalidDataException(
                $"Challenges {numbers} all use track {key.Track} with car {key.Car}. Wet and dry " +
                "is the only split the overlay can resolve.");

        if (sharing.Count == 2 && Math.Abs(sharing[0].BronzeSeconds - sharing[1].BronzeSeconds) < 0.001)
            throw new InvalidDataException(
                $"Challenges {numbers} use track {key.Track} with car {key.Car} and the same " +
                "targets. Nothing tells them apart.");
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

    /// <summary>Builds a catalog from rows already read, as the API path produces them.</summary>
    /// <param name="challenges">The challenges to index.</param>
    /// <returns>The catalog.</returns>
    /// <exception cref="InvalidDataException">A row is unusable, or a combination is ambiguous.</exception>
    public static ChallengeCatalog FromChallenges(IReadOnlyList<Challenge> challenges) => new(challenges);

    /// <summary>Matches a live session to a challenge, using wetness only to break a tie.</summary>
    /// <param name="trackId">WeekendInfo:TrackID.</param>
    /// <param name="carId">The player's DriverInfo:Drivers:CarID.</param>
    /// <param name="wet">Whether the session counts as wet.</param>
    /// <returns>The matching challenge, or null.</returns>
    public Challenge? Find(int trackId, int carId, bool wet) =>
        _byCombination.GetValueOrDefault((trackId, carId)) switch
        {
            null or [] => null,
            [var only] => only,
            var sharing => wet
                ? sharing.MaxBy(c => c.BronzeSeconds)
                : sharing.MinBy(c => c.BronzeSeconds),
        };

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
