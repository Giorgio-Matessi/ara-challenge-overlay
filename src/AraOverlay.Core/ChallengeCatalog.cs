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

    private readonly Dictionary<(string Track, string Car), Challenge> _byTrackAndCar;

    public IReadOnlyList<Challenge> Challenges { get; }

    private ChallengeCatalog(IReadOnlyList<Challenge> challenges)
    {
        Challenges = challenges;
        _byTrackAndCar = new Dictionary<(string, string), Challenge>();

        foreach (var challenge in challenges)
        {
            challenge.Validate();
            var key = Key(challenge.TrackId, challenge.CarId);
            if (!_byTrackAndCar.TryAdd(key, challenge))
                throw new InvalidDataException(
                    $"Challenges {_byTrackAndCar[key].Number} and {challenge.Number} share the same " +
                    $"track+car pair ('{challenge.TrackId}' / '{challenge.CarId}').");
        }
    }

    public static ChallengeCatalog Embedded { get; } = LoadEmbedded();

    public static ChallengeCatalog FromJson(string json)
    {
        var challenges = JsonSerializer.Deserialize<List<Challenge>>(json, JsonOptions)
            ?? throw new InvalidDataException("challenges.json did not contain a list.");
        return new ChallengeCatalog(challenges);
    }

    /// <summary>The challenge for this track+car pair, or null if the driver isn't on one.</summary>
    public Challenge? Find(string? trackId, string? carId)
    {
        if (string.IsNullOrWhiteSpace(trackId) || string.IsNullOrWhiteSpace(carId)) return null;
        return _byTrackAndCar.GetValueOrDefault(Key(trackId, carId));
    }

    private static (string, string) Key(string track, string car) =>
        (track.Trim().ToLowerInvariant(), car.Trim().ToLowerInvariant());

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
