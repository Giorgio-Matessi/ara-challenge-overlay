using System.Globalization;
using System.Text.Json;

namespace AraOverlay.Core;

/// <summary>What one plan payload yielded, and what it could not.</summary>
public sealed record ApiCatalogResult(IReadOnlyList<Challenge> Challenges, IReadOnlyList<string> Skipped);

/// <summary>
/// Turns a Labs training-plan payload into challenges.
///
/// Garage61 numbers its own tracks and cars, so every id the overlay needs comes off trackInfo
/// and carInfos after checking the platform is iRacing — never off the plan's own track/cars
/// fields. An item the overlay cannot read is reported rather than dropped: the contract is that
/// an upstream problem must never look like a shorter list of challenges.
/// </summary>
public static class ApiCatalog
{
    /// <summary>Reads the detail route's document.</summary>
    /// <param name="json">The body of GET /training-plans/{id}.</param>
    /// <returns>The challenges it describes, and a line per item that could not be read.</returns>
    /// <exception cref="InvalidDataException">The document is not a training plan.</exception>
    public static ApiCatalogResult FromPlanDetail(string json)
    {
        using var document = Parse(json);

        if (!document.RootElement.TryGetProperty("trainingPlan", out var plan) ||
            !plan.TryGetProperty("content", out var content) ||
            content.ValueKind != JsonValueKind.Array)
            throw new InvalidDataException("The response did not contain a training plan.");

        var challenges = new List<Challenge>();
        var skipped = new List<string>();
        var ordinal = 0;

        foreach (var item in content.EnumerateArray())
        {
            if (Text(item, "type") != "target_time") continue;
            ordinal++;
            Read(item, ordinal, challenges, skipped);
        }

        return new ApiCatalogResult(challenges, skipped);
    }

    /// <summary>Parses the body, turning malformed JSON into the same failure as a wrong shape.</summary>
    /// <param name="json">The response body.</param>
    /// <returns>The parsed document.</returns>
    /// <exception cref="InvalidDataException">The body is not JSON.</exception>
    private static JsonDocument Parse(string json)
    {
        try
        {
            return JsonDocument.Parse(json);
        }
        catch (JsonException e)
        {
            throw new InvalidDataException("The response was not valid JSON.", e);
        }
    }

    /// <summary>Reads one target-time item, adding a challenge per car or a reason it was skipped.</summary>
    /// <param name="item">The content item.</param>
    /// <param name="ordinal">Its place among the target-time items, used when position is absent.</param>
    /// <param name="challenges">Where to add what was read.</param>
    /// <param name="skipped">Where to add why it wasn't.</param>
    private static void Read(JsonElement item, int ordinal, List<Challenge> challenges, List<string> skipped)
    {
        var contentId = Text(item, "id") ?? "";
        var name = contentId.Length > 0 ? contentId : $"item {ordinal}";

        if (contentId.Length == 0)
        {
            skipped.Add($"Challenge {name} has no id.");
            return;
        }

        item.TryGetProperty("trackInfo", out var trackInfo);

        if (PlatformId(trackInfo) is not { } trackId)
        {
            skipped.Add($"Challenge {name} has no iRacing track mapping.");
            return;
        }

        if (Targets(item) is not { } targets)
        {
            skipped.Add($"Challenge {name} does not have all three targets.");
            return;
        }

        if (Cars(item) is not { Count: > 0 } cars)
        {
            skipped.Add($"Challenge {name} has no iRacing car mapping, or is open to every car.");
            return;
        }

        var number = item.TryGetProperty("position", out var position) && position.TryGetInt32(out var declared)
            ? declared
            : ordinal;

        var mapped = cars.Select(car => new Challenge
        {
            Number = number,
            ContentId = contentId,
            Track = Text(trackInfo, "name") ?? "",
            Car = car.Name,
            TrackIds = [trackId],
            CarId = car.Id,
            Gold = targets.Gold,
            Silver = targets.Silver,
            Bronze = targets.Bronze,
        }).ToList();

        // Checked here rather than left to the catalog, where one unusable row would throw the
        // other nineteen away.
        try
        {
            mapped.ForEach(c => c.Validate());
        }
        catch (InvalidDataException e)
        {
            skipped.Add($"Challenge {name} is unusable: {e.Message}");
            return;
        }

        challenges.AddRange(mapped);
    }

    /// <summary>Reads the cars an item applies to, as iRacing ids.</summary>
    /// <param name="item">The content item.</param>
    /// <returns>One entry per listed car, or null if any listed car has no iRacing mapping.</returns>
    private static List<(int Id, string Name)>? Cars(JsonElement item)
    {
        // An empty cars list means every car. Nothing in the series uses it and the panel has no
        // way to show it, so it falls through as unmapped and is reported rather than guessed at.
        if (!item.TryGetProperty("cars", out var cars) || cars.ValueKind != JsonValueKind.Array) return null;

        var infos = item.TryGetProperty("carInfos", out var carInfos) && carInfos.ValueKind == JsonValueKind.Array
            ? carInfos.EnumerateArray().ToList()
            : [];

        var mapped = new List<(int, string)>();

        foreach (var car in cars.EnumerateArray())
        {
            if (!car.TryGetInt32(out var garage61Id)) return null;

            var info = infos.Find(i => i.TryGetProperty("id", out var id) && id.TryGetInt32(out var v) && v == garage61Id);
            if (PlatformId(info) is not { } carId) return null;

            mapped.Add((carId, Text(info, "name") ?? ""));
        }

        return mapped;
    }

    /// <summary>Reads the three medal targets by their ids.</summary>
    /// <param name="item">The content item.</param>
    /// <returns>The targets as invariant seconds, or null if a tier is missing.</returns>
    private static (string Gold, string Silver, string Bronze)? Targets(JsonElement item)
    {
        if (!item.TryGetProperty("targets", out var targets) || targets.ValueKind != JsonValueKind.Array) return null;

        var byId = new Dictionary<string, string>();

        foreach (var target in targets.EnumerateArray())
            if (Text(target, "id") is { } id &&
                target.TryGetProperty("lapTime", out var lapTime) &&
                lapTime.TryGetDouble(out var seconds) &&
                seconds > 0)
                byId[id.ToLowerInvariant()] = seconds.ToString("R", CultureInfo.InvariantCulture);

        return byId.TryGetValue("gold", out var gold) &&
               byId.TryGetValue("silver", out var silver) &&
               byId.TryGetValue("bronze", out var bronze)
            ? (gold, silver, bronze)
            : null;
    }

    /// <summary>Reads a catalog entry's simulator id, if it is an iRacing one.</summary>
    /// <param name="info">A trackInfo or carInfos entry; may be null or absent.</param>
    /// <returns>The iRacing id, or null when the entry is missing, another sim, or unparseable.</returns>
    private static int? PlatformId(JsonElement info)
    {
        if (info.ValueKind != JsonValueKind.Object) return null;
        if (!string.Equals(Text(info, "platform"), "iracing", StringComparison.OrdinalIgnoreCase)) return null;
        if (!info.TryGetProperty("platform_id", out var platformId)) return null;

        // The schema allows either, and the published examples use a string.
        return platformId.ValueKind switch
        {
            JsonValueKind.Number => platformId.TryGetInt32(out var number) ? number : null,
            JsonValueKind.String => int.TryParse(platformId.GetString(), NumberStyles.Integer,
                CultureInfo.InvariantCulture, out var parsed) ? parsed : null,
            _ => null,
        };
    }

    /// <summary>Reads a string property.</summary>
    /// <param name="element">The object to read from.</param>
    /// <param name="name">The property name.</param>
    /// <returns>Its value, or null if absent or not a string.</returns>
    private static string? Text(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object &&
        element.TryGetProperty(name, out var value) &&
        value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
}
