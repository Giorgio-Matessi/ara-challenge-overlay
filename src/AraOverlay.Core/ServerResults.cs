using System.Text.Json;

namespace AraOverlay.Core;

/// <summary>One challenge's best lap as Garage61 recorded and graded it.</summary>
/// <param name="ContentId">The challenge's content id, which is its progress key.</param>
/// <param name="Seconds">The lap time.</param>
/// <param name="Medal">The server's verdict; None means confirmed no medal.</param>
public sealed record ServerBest(string ContentId, double Seconds, Medal Medal);

/// <summary>
/// Reads GET /training-plans/{id}/results, and the 409 it answers with when Garage61 isn't
/// linked. Nothing here throws: bests are a nice-to-have and must never cost the catalog.
/// </summary>
public static class ServerResults
{
    /// <summary>Reads the member's bests, keeping only those with a known verdict.</summary>
    /// <param name="json">The response body.</param>
    /// <returns>The bests, or null if the body isn't a results document.</returns>
    public static List<ServerBest>? Parse(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);

            if (document.RootElement.ValueKind != JsonValueKind.Object ||
                !document.RootElement.TryGetProperty("items", out var items) ||
                items.ValueKind != JsonValueKind.Array)
                return null;

            var bests = new List<ServerBest>();

            foreach (var item in items.EnumerateArray())
                if (ApiCatalog.Text(item, "contentId") is { Length: > 0 } contentId &&
                    item.TryGetProperty("best", out var best) &&
                    best.ValueKind == JsonValueKind.Object &&
                    best.TryGetProperty("lapTime", out var lapTime) &&
                    lapTime.TryGetDouble(out var seconds) &&
                    seconds > 0 &&
                    Verdict(best) is { } medal)
                    bests.Add(new ServerBest(contentId, seconds, medal));

            return bests;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// Reads targetReached, which is tri-state: null is unknown, "" is confirmed no medal, and
    /// anything else names a tier. Treating null as "" would wipe real progress.
    /// </summary>
    /// <param name="best">The best-lap object.</param>
    /// <returns>The medal, or null when the server doesn't know or names a tier we don't.</returns>
    private static Medal? Verdict(JsonElement best) =>
        ApiCatalog.Text(best, "targetReached")?.ToLowerInvariant() switch
        {
            "" => Medal.None,
            "bronze" => Medal.Bronze,
            "silver" => Medal.Silver,
            "gold" => Medal.Gold,
            _ => null,
        };

    /// <summary>Turns a 409 into a one-line hint and the page that fixes it.</summary>
    /// <param name="json">The 409's body.</param>
    /// <returns>The hint, and the link URL if it is https.</returns>
    public static (string Hint, string? Url) LinkHint(string json)
    {
        string? code = null, link = null;

        try
        {
            using var document = JsonDocument.Parse(json);
            code = ApiCatalog.Text(document.RootElement, "code");
            link = ApiCatalog.Text(document.RootElement, "linkUrl");
        }
        catch (JsonException)
        {
        }

        var hint = code switch
        {
            "G61_NOT_LINKED" => "Link Garage61 to your Academy account to bring back your medals.",
            "G61_RECONNECT_REQUIRED" => "Reconnect Garage61 to bring back your medals.",
            "G61_IDENTITY_MISMATCH" => "Your Garage61 account is different from the one linked to the Academy.",
            _ => "Garage61 isn't connected, so your medals can't be brought back.",
        };

        var url = Uri.TryCreate(link, UriKind.Absolute, out var parsed) && parsed.Scheme == Uri.UriSchemeHttps
            ? parsed.AbsoluteUri
            : null;

        return (hint, url);
    }
}
