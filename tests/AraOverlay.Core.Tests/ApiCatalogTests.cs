using AraOverlay.Core;

namespace AraOverlay.Core.Tests;

/// <summary>
/// Covers turning a Labs plan payload into challenges. Garage61's own ids are not iRacing's, so
/// almost every case here is about reading the right id off the right object.
/// </summary>
public class ApiCatalogTests
{
    private const string Targets = """
        { "id": "bronze", "label": "Bronze", "lapTime": 108.5 },
        { "id": "silver", "label": "Silver", "lapTime": 108 },
        { "id": "gold",   "label": "Gold",   "lapTime": 107.5 }
        """;

    private const string TrackInfo = """{ "id": 69, "name": "Example circuit", "platform": "iracing", "platform_id": "166" }""";
    private const string CarInfos = """[{ "id": 8, "name": "Example car", "platform": "iracing", "platform_id": "67" }]""";

    /// <summary>Builds one content item, with every awkward field overridable.</summary>
    /// <returns>A target-time content item as JSON.</returns>
    private static string Item(
        string id = "01KCRX69FW10CR0M7NYXX1CA2H",
        string type = "target_time",
        string position = "\"position\": 7,",
        string cars = "[8]",
        string targets = Targets,
        string trackInfo = TrackInfo,
        string carInfos = CarInfos) => $$"""
        {
          "id": "{{id}}",
          "type": "{{type}}",
          {{position}}
          "track": 69,
          "cars": {{cars}},
          "targets": [{{targets}}],
          "trackInfo": {{trackInfo}},
          "carInfos": {{carInfos}},
          "result": null
        }
        """;

    /// <summary>Wraps content items in the envelope the detail route returns.</summary>
    /// <param name="content">The content array's contents, as JSON.</param>
    /// <returns>A full plan detail document.</returns>
    private static string Plan(string content) => $$"""
        {
          "trainingPlanId": "01KCRX69A5AYWJ5AXFXAT9DJNW",
          "team": "almeida-racing-academy",
          "mode": "plan",
          "trainingPlan": { "id": "01KCRX69A5AYWJ5AXFXAT9DJNW", "name": "ARA", "content": [{{content}}] }
        }
        """;

    /// <summary>Reads one plan holding a single item.</summary>
    /// <param name="content">The item.</param>
    /// <returns>What the mapper made of it.</returns>
    private static ApiCatalogResult Read(string content) => ApiCatalog.FromPlanDetail(Plan(content));

    [Fact]
    public void ReadsTheIracingIdsAndTargets()
    {
        var challenge = Assert.Single(Read(Item()).Challenges);

        Assert.Equal([166], challenge.TrackIds);
        Assert.Equal(67, challenge.CarId);
        Assert.Equal(7, challenge.Number);
        Assert.Equal("01KCRX69FW10CR0M7NYXX1CA2H", challenge.ContentId);
        Assert.Equal("01KCRX69FW10CR0M7NYXX1CA2H", challenge.Key);
        Assert.Equal("Example circuit", challenge.Track);
        Assert.Equal("Example car", challenge.Car);
        Assert.Equal(107.5, challenge.GoldSeconds, 3);
        Assert.Equal(108.0, challenge.SilverSeconds, 3);
        Assert.Equal(108.5, challenge.BronzeSeconds, 3);
    }

    [Fact]
    public void AcceptsAPlatformIdThatArrivesAsANumber()
    {
        // The schema allows string or integer, and the published examples use a string.
        var numeric = Item(trackInfo: """{ "id": 69, "name": "C", "platform": "iracing", "platform_id": 166 }""");

        Assert.Equal([166], Assert.Single(Read(numeric).Challenges).TrackIds);
    }

    [Fact]
    public void MedalsComeFromTheTargetIdNotTheArrayOrder()
    {
        var shuffled = Item(targets: """
            { "id": "gold",   "label": "Gold",   "lapTime": 107.5 },
            { "id": "bronze", "label": "Bronze", "lapTime": 108.5 },
            { "id": "silver", "label": "Silver", "lapTime": 108 }
            """);

        var challenge = Assert.Single(Read(shuffled).Challenges);
        Assert.Equal(107.5, challenge.GoldSeconds, 3);
        Assert.Equal(108.5, challenge.BronzeSeconds, 3);
    }

    [Fact]
    public void SkipsContentThatIsNotATargetTimeChallengeWithoutComplaining()
    {
        // Plans hold notes and other content types; those are not failures.
        var result = Read(Item(type: "text"));

        Assert.Empty(result.Challenges);
        Assert.Empty(result.Skipped);
    }

    [Theory]
    [InlineData("""{ "id": 69, "name": "C", "platform": "acc", "platform_id": "166" }""")]
    [InlineData("null")]
    [InlineData("""{ "id": 69, "name": "C", "platform": "iracing" }""")]
    [InlineData("""{ "id": 69, "name": "C", "platform": "iracing", "platform_id": "not-a-number" }""")]
    public void SkipsAndReportsAChallengeWithNoUsableTrack(string trackInfo)
    {
        var result = Read(Item(trackInfo: trackInfo));

        Assert.Empty(result.Challenges);
        Assert.Single(result.Skipped);
    }

    [Fact]
    public void SkipsAChallengeWhoseCarHasNoIracingMapping()
    {
        // The docs are explicit: a listed car with no mapping is an error, never "any car".
        var result = Read(Item(carInfos: "[]"));

        Assert.Empty(result.Challenges);
        Assert.Single(result.Skipped);
    }

    [Fact]
    public void SkipsAChallengeOpenToEveryCarRatherThanGuessing()
    {
        // An empty cars list means all cars. No ARA challenge uses it and the panel has no way to
        // show one, so it is reported rather than silently dropped or treated as a single car.
        var result = Read(Item(cars: "[]"));

        Assert.Empty(result.Challenges);
        Assert.Single(result.Skipped);
    }

    [Fact]
    public void AChallengeListingSeveralCarsBecomesOneRowPerCar()
    {
        var twoCars = Item(
            cars: "[8, 9]",
            carInfos: """
                [{ "id": 8, "name": "A", "platform": "iracing", "platform_id": "67" },
                 { "id": 9, "name": "B", "platform": "iracing", "platform_id": "68" }]
                """);

        var challenges = Read(twoCars).Challenges;

        Assert.Equal([67, 68], challenges.Select(c => c.CarId));
        Assert.Equal(["A", "B"], challenges.Select(c => c.Car));
        Assert.All(challenges, c => Assert.Equal("01KCRX69FW10CR0M7NYXX1CA2H", c.ContentId));
    }

    [Fact]
    public void SkipsAChallengeOneOfWhoseCarsIsUnmapped()
    {
        var partial = Item(
            cars: "[8, 9]",
            carInfos: """[{ "id": 8, "name": "A", "platform": "iracing", "platform_id": "67" }]""");

        var result = Read(partial);

        Assert.Empty(result.Challenges);
        Assert.Single(result.Skipped);
    }

    [Theory]
    [InlineData("""{ "id": "bronze", "lapTime": 108.5 }, { "id": "silver", "lapTime": 108 }""")]
    [InlineData("""{ "id": "gold", "lapTime": 107.5 }""")]
    [InlineData("")]
    public void SkipsAChallengeMissingATier(string targets)
    {
        var result = Read(Item(targets: targets));

        Assert.Empty(result.Challenges);
        Assert.Single(result.Skipped);
    }

    [Fact]
    public void SkipsARowWithTargetsOutOfOrderRatherThanLosingThePlan()
    {
        // Validation happens per item: one retuned target that came out backwards must not throw
        // the other nineteen challenges away.
        var backwards = Item(targets: """
            { "id": "bronze", "lapTime": 107.5 },
            { "id": "silver", "lapTime": 108 },
            { "id": "gold",   "lapTime": 108.5 }
            """);

        var result = ApiCatalog.FromPlanDetail(Plan($"{Item()},{backwards}"));

        Assert.Single(result.Challenges);
        Assert.Single(result.Skipped);
    }

    [Fact]
    public void NumbersByOrdinalWhenThePlanHasNoPositions()
    {
        var first = Item(position: "");
        var second = Item(
            id: "01KCRX69FZRAKX8V3QKYS5EACT",
            position: "",
            trackInfo: """{ "id": 70, "name": "Other", "platform": "iracing", "platform_id": "167" }""");

        var challenges = ApiCatalog.FromPlanDetail(Plan($"{first},{second}")).Challenges;

        Assert.Equal([1, 2], challenges.Select(c => c.Number));
    }

    [Fact]
    public void OrdinalsCountOnlyChallengesNotEveryContentItem()
    {
        var note = Item(id: "note", type: "text", position: "");
        var challenge = Item(position: "");

        var challenges = ApiCatalog.FromPlanDetail(Plan($"{note},{challenge}")).Challenges;

        Assert.Equal(1, Assert.Single(challenges).Number);
    }

    [Fact]
    public void ToleratesUnknownFieldsAndAnEmptyPlan()
    {
        Assert.Empty(ApiCatalog.FromPlanDetail(Plan("")).Challenges);

        var extra = Item(position: "\"position\": 7, \"somethingNew\": { \"a\": 1 },");
        Assert.Single(Read(extra).Challenges);
    }

    [Fact]
    public void AMappedPlanFeedsTheCatalogStraightBack()
    {
        // The whole point: what comes off the API has to match the same way the embedded rows do.
        var catalog = new ChallengeCatalog(Read(Item()).Challenges);

        Assert.Equal(7, Assert.Single(catalog.Find(166, 67, wet: false)).Number);
        Assert.Empty(catalog.Find(166, 999, wet: false));
    }

    [Theory]
    [InlineData("""{ "error": "Authentication required" }""")]
    [InlineData("""{ "trainingPlan": { "id": "x" } }""")]
    [InlineData("not json at all")]
    public void RejectsADocumentThatIsNotAPlan(string body)
    {
        Assert.Throws<InvalidDataException>(() => ApiCatalog.FromPlanDetail(body));
    }
}
