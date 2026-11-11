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

    /// <summary>The member's Garage61 bests across every plan; null if not asked for or any plan's failed.</summary>
    public IReadOnlyList<ServerBest>? Bests { get; init; }

    /// <summary>Why bests are missing when Garage61 isn't linked, as a line for the member.</summary>
    public string? LinkHint { get; init; }

    /// <summary>The https page that links Garage61, when the server gave one.</summary>
    public string? LinkUrl { get; init; }

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
    /// <param name="withBests">Also read the member's Garage61 bests for every plan.</param>
    /// <param name="cancel">Cancels the refresh.</param>
    /// <returns>The challenges, or a reason the refresh failed.</returns>
    public static async Task<CatalogFetch> Fetch(ApiClient api, string token, bool withBests = false, CancellationToken cancel = default)
    {
        var challenges = new List<Challenge>();
        var skipped = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var bests = withBests ? new List<ServerBest>() : null;
        (string Hint, string? Url)? link = null;

        try
        {
            if (await PlanIds(api, token, cancel).ConfigureAwait(false) is not { } plans)
                return CatalogFetch.Failed(Describe((await api.GetSession(token, cancel).ConfigureAwait(false)).Status));

            foreach (var planId in plans)
            {
                var response = await api.GetPlan(token, planId, cancel).ConfigureAwait(false);
                if (!response.Ok) return CatalogFetch.Failed(Describe(response.Status));

                var read = ApiCatalog.FromPlanDetail(response.Body);
                skipped.AddRange(read.Skipped);

                // A plan set holds regular and weekly challenges, and the same one can appear in
                // more than one plan; a second copy of a content id is that, not a second challenge.
                foreach (var challenge in read.Challenges)
                    if (seen.Add(challenge.ContentId)) challenges.Add(challenge);
                    else skipped.Add($"Challenge {challenge.ContentId} appears in more than one plan; keeping the first.");

                if (bests is null) continue;

                // All or nothing: the merge is server-wins, so half a set would seed once and stick.
                var (found, conflict) = await Bests(api, token, planId, cancel).ConfigureAwait(false);
                if (found is not null) { bests.AddRange(found); continue; }

                bests = null;
                link = conflict;
                if (conflict is null) skipped.Add("Personal bests could not be read from the Academy; keeping local progress.");
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

        return new CatalogFetch(challenges, skipped, null) { Bests = bests, LinkHint = link?.Hint, LinkUrl = link?.Url };
    }

    /// <summary>Reads one plan's bests. Never throws: bests must not cost the catalog.</summary>
    /// <param name="api">The Labs client.</param>
    /// <param name="token">The ARA bearer token.</param>
    /// <param name="planId">The plan to read.</param>
    /// <param name="cancel">Cancels the refresh.</param>
    /// <returns>The bests, or null with the 409's hint when Garage61 isn't linked.</returns>
    private static async Task<(List<ServerBest>? Bests, (string Hint, string? Url)? Link)> Bests(
        ApiClient api, string token, string planId, CancellationToken cancel)
    {
        try
        {
            var response = await api.GetResults(token, planId, cancel).ConfigureAwait(false);

            if (response.Status == 409) return (null, ServerResults.LinkHint(response.Body));
            return (response.Ok ? ServerResults.Parse(response.Body) : null, null);
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException && !cancel.IsCancellationRequested)
        {
            return (null, null);
        }
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
