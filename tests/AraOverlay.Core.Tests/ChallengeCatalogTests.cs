using System.Text.Json;
using AraOverlay.Core;

namespace AraOverlay.Core.Tests;

/// <summary>Covers matching a session against the catalog, and the guards on a bad row.</summary>
public class ChallengeCatalogTests
{
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    /// <summary>Builds a catalog from a JSON list of challenges.</summary>
    /// <param name="json">The rows.</param>
    /// <returns>The catalog.</returns>
    private static ChallengeCatalog FromJson(string json) =>
        new(JsonSerializer.Deserialize<List<Challenge>>(json, JsonOptions)!);

    private const string TwoRows = """
    [
      { "number": 1, "trackIds": [299], "carId": 142,
        "gold": "0:53.500", "silver": "0:54.200", "bronze": "0:55.000" },
      { "number": 2, "trackIds": [181], "carId": 67,
        "gold": "2:30.000", "silver": "2:32.000", "bronze": "2:34.000" }
    ]
    """;

    [Fact]
    public void Find_MatchesOnTrackAndCar()
    {
        var catalog = FromJson(TwoRows);
        Assert.Equal(1, Assert.Single(catalog.Find(299, 142, wet: false)).Number);
        Assert.Equal(2, Assert.Single(catalog.Find(181, 67, wet: false)).Number);
    }

    [Theory]
    [InlineData(299, 67)]
    [InlineData(181, 142)]
    [InlineData(500, 142)]
    [InlineData(0, 0)]
    [InlineData(-1, -1)]
    public void Find_ReturnsNothingWhenNothingMatches(int track, int car)
    {
        Assert.Empty(FromJson(TwoRows).Find(track, car, wet: false));
    }

    [Fact]
    public void FromJson_KeepsTheFirstOfTwoChallengesItCannotTellApart()
    {
        // Same track and car, same times: either one grades a lap identically, so the second is
        // reported rather than costing the whole catalog.
        const string duplicated = """
        [
          { "number": 1, "trackIds": [1], "carId": 2,
            "gold": "1:00.000", "silver": "1:01.000", "bronze": "1:02.000" },
          { "number": 2, "trackIds": [1], "carId": 2,
            "gold": "1:00.000", "silver": "1:01.000", "bronze": "1:02.000" }
        ]
        """;
        var catalog = FromJson(duplicated);

        Assert.Equal(1, Assert.Single(catalog.Challenges).Number);
        Assert.Equal(1, Assert.Single(catalog.Find(1, 2, wet: false)).Number);
        Assert.Contains("keeping the first", Assert.Single(catalog.Skipped));
    }

    [Fact]
    public void FromJson_RejectsThreeChallengesOnTheSameCombination()
    {
        // Wet and dry is the only split the overlay can resolve; a third would be a new rule.
        const string three = """
        [
          { "number": 1, "trackIds": [1], "carId": 2,
            "gold": "1:00.000", "silver": "1:01.000", "bronze": "1:02.000" },
          { "number": 2, "trackIds": [1], "carId": 2,
            "gold": "2:00.000", "silver": "2:01.000", "bronze": "2:02.000" },
          { "number": 3, "trackIds": [1], "carId": 2,
            "gold": "3:00.000", "silver": "3:01.000", "bronze": "3:02.000" }
        ]
        """;
        Assert.Throws<InvalidDataException>(() => { FromJson(three); });
    }

    [Fact]
    public void WetAndDryVersionsOfTheSameCombinationAreToldApartByTheirTargets()
    {
        // This is challenges 14 and 19: the same car, on the same Le Mans layout. Nothing in the
        // data says which is wet — the wet one is 34 seconds slower, and that's the whole signal.
        const string lemans = """
        [
          { "number": 14, "trackIds": [268], "carId": 128,
            "gold": "3:36.250", "silver": "3:37.000", "bronze": "3:38.800" },
          { "number": 19, "trackIds": [268], "carId": 128,
            "gold": "4:10.200", "silver": "4:11.200", "bronze": "4:13.700" }
        ]
        """;

        var catalog = FromJson(lemans);
        Assert.Equal(14, Assert.Single(catalog.Find(268, 128, wet: false)).Number);
        Assert.Equal(19, Assert.Single(catalog.Find(268, 128, wet: true)).Number);
    }

    [Fact]
    public void TheOnlyChallengeForACombinationMatchesInAnyWeather()
    {
        // Challenges only count in a league session, where ARA sets the weather, so a lone
        // challenge for a track and car is the one being run whatever the sky is doing.
        var catalog = FromJson(TwoRows);
        Assert.Equal(1, Assert.Single(catalog.Find(299, 142, wet: true)).Number);
        Assert.Equal(1, Assert.Single(catalog.Find(299, 142, wet: false)).Number);
    }

    [Fact]
    public void AChallengeCanBeListedOnSeveralBuildsOfTheSameCircuit()
    {
        // This is challenges 11 and 18: the league counts either Spa layout.
        const string spa = """
        [
          { "number": 11, "trackIds": [523, 163], "carId": 128,
            "gold": "2:07.900", "silver": "2:08.700", "bronze": "2:09.700" }
        ]
        """;

        var catalog = FromJson(spa);
        Assert.Equal(11, Assert.Single(catalog.Find(523, 128, wet: false)).Number);
        Assert.Equal(11, Assert.Single(catalog.Find(163, 128, wet: false)).Number);
        Assert.Empty(catalog.Find(268, 128, wet: false));
    }

    [Fact]
    public void TwoPlansSharingATrackAndCarBothCount()
    {
        // ARA runs several series and they share circuits. Both are real challenges a driver
        // could be attempting and no telemetry says which, so the lookup offers both.
        const string twoPlans = """
        [
          { "number": 17, "plan": "ARA Challenges", "trackIds": [9], "carId": 67,
            "gold": "1:30.600", "silver": "1:31.150", "bronze": "1:31.950" },
          { "number": 3, "plan": "MX-5 Series", "trackIds": [9], "carId": 67,
            "gold": "1:20.475", "silver": "1:20.750", "bronze": "1:21.500" }
        ]
        """;

        var catalog = FromJson(twoPlans);

        Assert.Equal([17, 3], catalog.Find(9, 67, wet: false).Select(c => c.Number));
        Assert.Equal([17, 3], catalog.Find(9, 67, wet: true).Select(c => c.Number));
    }

    [Fact]
    public void WetnessOnlyDecidesWithinOnePlan()
    {
        // The slower row is the wet one of its own plan, never another plan's challenge.
        const string mixed = """
        [
          { "number": 14, "plan": "ARA Challenges", "trackIds": [268], "carId": 128,
            "gold": "3:36.250", "silver": "3:37.000", "bronze": "3:38.800" },
          { "number": 19, "plan": "ARA Challenges", "trackIds": [268], "carId": 128,
            "gold": "4:10.200", "silver": "4:11.200", "bronze": "4:13.700" },
          { "number": 12, "plan": "MX-5 Series", "trackIds": [268], "carId": 128,
            "gold": "5:20.500", "silver": "5:23.000", "bronze": "5:28.500" }
        ]
        """;

        var catalog = FromJson(mixed);

        Assert.Equal([14, 12], catalog.Find(268, 128, wet: false).Select(c => c.Number));
        Assert.Equal([19, 12], catalog.Find(268, 128, wet: true).Select(c => c.Number));
    }

    [Fact]
    public void FromJson_RejectsARowWithNoTrackIds()
    {
        const string none = """
        [
          { "number": 1, "trackIds": [], "carId": 2,
            "gold": "1:00.000", "silver": "1:01.000", "bronze": "1:02.000" }
        ]
        """;
        Assert.Throws<InvalidDataException>(() => { FromJson(none); });
    }

    [Fact]
    public void FromJson_RejectsTimesThatAreOutOfOrder()
    {
        const string backwards = """
        [
          { "number": 1, "trackIds": [1], "carId": 2,
            "gold": "1:05.000", "silver": "1:01.000", "bronze": "1:02.000" }
        ]
        """;
        Assert.Throws<InvalidDataException>(() => { FromJson(backwards); });
    }
}
