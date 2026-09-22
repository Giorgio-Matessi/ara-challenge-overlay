using System.Text.Json;

namespace AraOverlay.Core;

/// <summary>The result of one refresh.</summary>
/// <param name="Challenges">Everything read, across every plan.</param>
/// <param name="Skipped">A line per content item that could not be read.</param>
/// <param name="Error">Why the refresh failed, or null when it didn't.</param>
public sealed record CatalogFetch(
    IReadOnlyList<Challenge> Challenges,
    IReadOnlyList<string> Skipped,
    string? Error)
{
    /// <summary>Whether this refresh produced a catalog worth keeping.</summary>
    public bool Usable => Error is null && Challenges.Count > 0;

    /// <summary>Builds a failed refresh.</summary>
    /// <param name="error">What went wrong.</param>
    /// <returns>A result carrying nothing but the reason.</returns>
    public static CatalogFetch Failed(string error) => new([], [], error);
}

/// <summary>
/// Reads every training plan the member can see and turns them into one set of challenges.
///
/// A refresh either works or says why. It never returns a short list: a plan that fails to load
/// would silently remove challenges from the overlay, which the contract forbids and which would
/// look to a driver exactly like a challenge being retired.
/// </summary>
public static class CatalogFetcher
{
    private const int PageSize = 100;
    private const int MostPages = 20;

    /// <summary>Fetches the whole catalog.</summary>
    /// <param name="api">The Labs client.</param>
    /// <param name="token">The ARA bearer token.</param>
    /// <param name="cancel">Cancels the refresh.</param>
    /// <returns>The challenges, or a reason the refresh failed.</returns>
    public static async Task<CatalogFetch> Fetch(ApiClient api, string token, CancellationToken cancel = default)
    {
        var challenges = new List<Challenge>();
        var skipped = new List<string>();

        try
        {
            if (await PlanIds(api, token, cancel).ConfigureAwait(false) is not { } plans)
                return CatalogFetch.Failed(await Status(api, token, cancel).ConfigureAwait(false));

            foreach (var planId in plans)
            {
                var response = await api.GetPlan(token, planId, cancel).ConfigureAwait(false);
                if (!response.Ok) return CatalogFetch.Failed(Describe(response.Status));

                var read = ApiCatalog.FromPlanDetail(response.Body);
                challenges.AddRange(read.Challenges);
                skipped.AddRange(read.Skipped);
            }
        }
        catch (Exception e) when (e is InvalidDataException or JsonException)
        {
            return CatalogFetch.Failed($"The Academy sent something unreadable: {e.Message}");
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException && !cancel.IsCancellationRequested)
        {
            return CatalogFetch.Failed("The Academy could not be reached.");
        }

        return new CatalogFetch(Distinct(challenges, skipped), skipped, null);
    }

    /// <summary>
    /// Drops challenges the lookup could not tell apart, reporting each one.
    ///
    /// A plan set holds regular and weekly challenges, and the same challenge can appear in more
    /// than one plan; a second copy of a content id is that, not a second challenge. Beyond that,
    /// two different challenges sharing a track, a car and their targets grade a lap identically,
    /// so keeping the first is harmless where rejecting the whole catalog would not be.
    /// </summary>
    /// <param name="challenges">Everything read, in plan order.</param>
    /// <param name="skipped">Where to record what was dropped and why.</param>
    /// <returns>The challenges the catalog can index.</returns>
    private static List<Challenge> Distinct(List<Challenge> challenges, List<string> skipped)
    {
        var kept = new List<Challenge>();
        var seenContent = new HashSet<string>(StringComparer.Ordinal);
        var byCombination = new Dictionary<(int Track, int Car), List<Challenge>>();

        foreach (var challenge in challenges)
        {
            if (!seenContent.Add(challenge.ContentId))
            {
                skipped.Add($"Challenge {challenge.ContentId} appears in more than one plan; keeping the first.");
                continue;
            }

            // Only within the same plan. Two plans using the same circuit and car are two real
            // challenges a driver could be attempting, and the lookup offers both rather than
            // picking one on their behalf.
            var twin = challenge.TrackIds
                .Select(track => byCombination.GetValueOrDefault((track, challenge.CarId)))
                .OfType<List<Challenge>>()
                .SelectMany(sharing => sharing)
                .FirstOrDefault(other =>
                    string.Equals(other.Plan, challenge.Plan, StringComparison.Ordinal) &&
                    Math.Abs(other.BronzeSeconds - challenge.BronzeSeconds) < 0.001);

            if (twin is not null)
            {
                skipped.Add(
                    $"Challenges {twin.ContentId} and {challenge.ContentId} share track " +
                    $"{challenge.TrackIds[0]}, car {challenge.CarId} and their targets; keeping the first.");
                continue;
            }

            foreach (var track in challenge.TrackIds)
            {
                if (!byCombination.TryGetValue((track, challenge.CarId), out var sharing))
                    byCombination[(track, challenge.CarId)] = sharing = [];
                sharing.Add(challenge);
            }

            kept.Add(challenge);
        }

        return kept;
    }

    /// <summary>Lists every plan id, following pagination to the end.</summary>
    /// <param name="api">The Labs client.</param>
    /// <param name="token">The ARA bearer token.</param>
    /// <param name="cancel">Cancels the refresh.</param>
    /// <returns>The ids, or null when a page failed.</returns>
    private static async Task<List<string>?> PlanIds(ApiClient api, string token, CancellationToken cancel)
    {
        var ids = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        for (var page = 0; page < MostPages; page++)
        {
            var response = await api.GetPlans(token, PageSize, ids.Count, cancel).ConfigureAwait(false);
            if (!response.Ok) return null;

            using var document = JsonDocument.Parse(response.Body);

            if (!document.RootElement.TryGetProperty("items", out var items) ||
                items.ValueKind != JsonValueKind.Array)
                throw new InvalidDataException("the plan list had no items.");

            var before = ids.Count;

            foreach (var item in items.EnumerateArray())
                if (item.ValueKind == JsonValueKind.Object &&
                    item.TryGetProperty("id", out var id) &&
                    id.ValueKind == JsonValueKind.String &&
                    id.GetString() is { Length: > 0 } text &&
                    seen.Add(text))
                    ids.Add(text);

            var total = document.RootElement.TryGetProperty("total", out var count) &&
                        count.TryGetInt32(out var read)
                ? read
                : ids.Count;

            // Stops on a page that added nothing new, so a server that ignores offset and keeps
            // returning the same page can't spin here. Deduping also keeps a repeated plan from
            // reaching the catalog twice, where it would trip the ambiguity guard.
            if (ids.Count >= total || ids.Count == before) break;
        }

        return ids;
    }

    /// <summary>Asks why a listing failed, so the member is told the actual reason.</summary>
    /// <param name="api">The Labs client.</param>
    /// <param name="token">The ARA bearer token.</param>
    /// <param name="cancel">Cancels the request.</param>
    /// <returns>A line for the panel.</returns>
    private static async Task<string> Status(ApiClient api, string token, CancellationToken cancel)
    {
        var session = await api.GetSession(token, cancel).ConfigureAwait(false);
        return Describe(session.Status);
    }

    /// <summary>Turns a status into something worth showing a driver.</summary>
    /// <param name="status">The HTTP status.</param>
    /// <returns>A line for the panel.</returns>
    private static string Describe(int status) => status switch
    {
        401 => "Your session expired. Sign in again from the tray.",
        403 => "An eligible Gold membership is required.",

        // Explicitly not a reason to sign in again: the fault is upstream of the member.
        502 => "The Academy's challenge data is unavailable.",
        503 => "The Academy's API is paused. Your saved challenges are still in use.",
        504 => "The Academy took too long to answer.",
        _ => $"The Academy's API returned {status}.",
    };
}
