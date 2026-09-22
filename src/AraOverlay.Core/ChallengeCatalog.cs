using System.Reflection;
using System.Text.Json;

namespace AraOverlay.Core;

/// <summary>
/// The 20 ARA challenges, embedded in the exe as challenges.json and indexed for lookup by
/// track and car.
///
/// Wet is deliberately not part of the key. The ARA Labs API carries no weather field and cannot
/// gain one, so a challenge's conditions are read off its targets: within one plan, a track and
/// car used twice is the wet and dry pair, and the wet one is slower. The league session sets the
/// weather anyway.
///
/// Across plans it means nothing. ARA runs several series and they share circuits and cars — two
/// plans both using Summit Point in an MX-5 are two different dry challenges, and no telemetry
/// can say which one a driver is attempting. So a lookup returns one candidate per plan and the
/// driver picks.
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

        foreach (var (key, sharing) in _byCombination)
            foreach (var plan in sharing.GroupBy(c => c.Plan, StringComparer.Ordinal))
                Ambiguous(key, [.. plan]);
    }

    /// <summary>Rejects a combination the overlay could not resolve from a live session.</summary>
    /// <param name="key">The track and car they share.</param>
    /// <param name="sharing">The challenges filed under it.</param>
    /// <exception cref="InvalidDataException">More than two, or two with the same targets.</exception>
    private static void Ambiguous((int Track, int Car) key, List<Challenge> sharing)
    {
        // Named by key as well as number: a catalog from the API is numbered by position, so two
        // rows can both be "challenge 1" and the number alone wouldn't find them.
        var named = string.Join(" and ", sharing.Select(c => $"{c.Number} ({c.Key})"));

        if (sharing.Count > 2)
            throw new InvalidDataException(
                $"Challenges {named} all use track {key.Track} with car {key.Car} in the same " +
                "plan. Wet and dry is the only split the overlay can resolve.");

        if (sharing.Count == 2 && Math.Abs(sharing[0].BronzeSeconds - sharing[1].BronzeSeconds) < 0.001)
            throw new InvalidDataException(
                $"Challenges {named} use track {key.Track} with car {key.Car} and the same " +
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

    /// <summary>
    /// Matches a live session, returning one candidate per plan. Wetness picks between a plan's
    /// own wet and dry rows; it says nothing about two plans that happen to share a circuit.
    /// </summary>
    /// <param name="trackId">WeekendInfo:TrackID.</param>
    /// <param name="carId">The player's DriverInfo:Drivers:CarID.</param>
    /// <param name="wet">Whether the session counts as wet.</param>
    /// <returns>A challenge per plan that uses this track and car, in plan order. May be empty.</returns>
    public IReadOnlyList<Challenge> Find(int trackId, int carId, bool wet)
    {
        if (_byCombination.GetValueOrDefault((trackId, carId)) is not { Count: > 0 } sharing)
            return [];

        return
        [
            .. sharing
                .GroupBy(c => c.Plan, StringComparer.Ordinal)
                .Select(plan => plan.Count() == 1
                    ? plan.First()
                    : wet
                        ? plan.MaxBy(c => c.BronzeSeconds)!
                        : plan.MinBy(c => c.BronzeSeconds)!)
        ];
    }

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
