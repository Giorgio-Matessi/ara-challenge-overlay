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

    private readonly Dictionary<(string Track, string Car, bool Wet), Challenge> _byCombination;

    public IReadOnlyList<Challenge> Challenges { get; }

    private ChallengeCatalog(IReadOnlyList<Challenge> challenges)
    {
        Challenges = challenges;
        _byCombination = new Dictionary<(string, string, bool), Challenge>();

        foreach (var challenge in challenges)
        {
            challenge.Validate();
            var key = Key(challenge.TrackId, challenge.CarId, challenge.Wet);
            if (!_byCombination.TryAdd(key, challenge))
                throw new InvalidDataException(
                    $"Challenges {_byCombination[key].Number} and {challenge.Number} share the same " +
                    $"track+car+conditions ('{challenge.TrackId}' / '{challenge.CarId}' / " +
                    $"{(challenge.Wet ? "wet" : "dry")}). The overlay could not tell them apart.");
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
    public Challenge? Find(string? trackId, string? carId, bool wet)
    {
        if (string.IsNullOrWhiteSpace(trackId) || string.IsNullOrWhiteSpace(carId)) return null;
        return _byCombination.GetValueOrDefault(Key(trackId, carId, wet));
    }

    private static (string, string, bool) Key(string track, string car, bool wet) =>
        (track.Trim().ToLowerInvariant(), car.Trim().ToLowerInvariant(), wet);

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
