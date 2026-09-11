using System.Reflection;
using System.Text.Json;

namespace AraOverlay.Core;

/// <summary>The 20 ARA challenges, baked into the exe as an embedded challenges.json.</summary>
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

    public static ChallengeCatalog FromJson(string json)
    {
        var challenges = JsonSerializer.Deserialize<List<Challenge>>(json, JsonOptions)
            ?? throw new InvalidDataException("challenges.json did not contain a list.");
        return new ChallengeCatalog(challenges);
    }

    /// <summary>The challenge for this track, car and condition, or null if there isn't one.</summary>
    public Challenge? Find(int trackId, int carId, bool wet) =>
        _byCombination.GetValueOrDefault((trackId, carId, wet));

    private static ChallengeCatalog LoadEmbedded()
    {
        var assembly = Assembly.GetExecutingAssembly();
        // Matched by suffix so renaming the root namespace doesn't silently break the lookup.
        var name = Array.Find(assembly.GetManifestResourceNames(), n => n.EndsWith("challenges.json", StringComparison.Ordinal))
            ?? throw new InvalidDataException("challenges.json is not embedded in the assembly.");

        using var stream = assembly.GetManifestResourceStream(name)!;
        using var reader = new StreamReader(stream);
        return FromJson(reader.ReadToEnd());
    }
}
