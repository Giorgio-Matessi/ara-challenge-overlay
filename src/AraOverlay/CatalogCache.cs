using System.IO;
using AraOverlay.Core;

namespace AraOverlay;

/// <summary>The last catalog read from the API, as it is held on disk.</summary>
/// <param name="Challenges">What it held.</param>
public sealed record CachedCatalog(List<Challenge> Challenges);

/// <summary>
/// Keeps the last good catalog under %APPDATA% so the overlay opens on real targets rather than
/// waiting on a refresh, and keeps working through an outage, an expired session, or a flight
/// with no internet. It is not secret: the same data ships embedded in the exe.
/// </summary>
public static class CatalogCache
{
    private static string FilePath => JsonFile.PathIn("challenges.cache.json");

    /// <summary>Replaces the cache with a fresh read.</summary>
    /// <param name="challenges">What the API returned.</param>
    public static void Save(IReadOnlyList<Challenge> challenges) =>
        JsonFile.Save(FilePath, new CachedCatalog([.. challenges]));

    /// <summary>
    /// Resolves what the overlay should actually use: the cache when it loads, and the embedded
    /// rows otherwise. A cache the catalog rejects is treated as absent rather than fatal — a bad
    /// file must not stop the overlay starting.
    /// </summary>
    /// <returns>The catalog to run on.</returns>
    public static ChallengeCatalog Resolve()
    {
        try
        {
            if (JsonFile.Load<CachedCatalog>(FilePath) is { Challenges.Count: > 0 } cached)
                return new ChallengeCatalog(cached.Challenges);
        }
        catch (InvalidDataException)
        {
        }

        return ChallengeCatalog.Embedded;
    }
}
