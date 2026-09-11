using AraOverlay.Core;

namespace AraOverlay.Core.Tests;

public class ChallengeCatalogTests
{
    private const string TwoRows = """
    [
      { "number": 1, "name": "A", "trackId": "limerock full", "carId": "mx5 mx52016",
        "gold": "0:53.500", "silver": "0:54.200", "bronze": "0:55.000" },
      { "number": 2, "name": "B", "trackId": "spa gp", "carId": "formulaf1600",
        "gold": "2:30.000", "silver": "2:32.000", "bronze": "2:34.000" }
    ]
    """;

    [Fact]
    public void Find_MatchesOnTrackAndCar()
    {
        var catalog = ChallengeCatalog.FromJson(TwoRows);
        Assert.Equal(1, catalog.Find("limerock full", "mx5 mx52016", wet: false)!.Number);
        Assert.Equal(2, catalog.Find("spa gp", "formulaf1600", wet: false)!.Number);
    }

    [Theory]
    [InlineData("LIMEROCK FULL", "MX5 MX52016")]
    [InlineData("  limerock full  ", " mx5 mx52016 ")]
    [InlineData("LimeRock Full", "Mx5 Mx52016")]
    public void Find_IgnoresCaseAndSurroundingWhitespace(string track, string car)
    {
        Assert.Equal(1, ChallengeCatalog.FromJson(TwoRows).Find(track, car, wet: false)!.Number);
    }

    [Theory]
    [InlineData("limerock full", "formulaf1600")]   // right track, wrong car
    [InlineData("spa gp", "mx5 mx52016")]           // right car, wrong track
    [InlineData("monza full", "mx5 mx52016")]
    [InlineData(null, null)]
    [InlineData("", "")]
    public void Find_ReturnsNullWhenNothingMatches(string? track, string? car)
    {
        Assert.Null(ChallengeCatalog.FromJson(TwoRows).Find(track, car, wet: false));
    }

    [Fact]
    public void FromJson_RejectsDuplicatesWithTheSameConditions()
    {
        const string duplicated = """
        [
          { "number": 1, "name": "A", "trackId": "t", "carId": "c",
            "gold": "1:00.000", "silver": "1:01.000", "bronze": "1:02.000" },
          { "number": 2, "name": "B", "trackId": "T", "carId": "C",
            "gold": "1:00.000", "silver": "1:01.000", "bronze": "1:02.000" }
        ]
        """;
        Assert.Throws<InvalidDataException>(() => { ChallengeCatalog.FromJson(duplicated); });
    }

    [Fact]
    public void WetAndDryVersionsOfTheSameCombinationAreDistinct()
    {
        // This is challenges 14 and 19: the same car, on the same Le Mans layout.
        const string lemans = """
        [
          { "number": 14, "name": "Le Mans dry", "trackId": "lemans 24h", "carId": "dallarap217",
            "gold": "3:36.250", "silver": "3:37.000", "bronze": "3:38.800" },
          { "number": 19, "name": "Le Mans wet", "trackId": "lemans 24h", "carId": "dallarap217",
            "wet": true,
            "gold": "4:10.200", "silver": "4:11.200", "bronze": "4:13.700" }
        ]
        """;

        var catalog = ChallengeCatalog.FromJson(lemans);
        Assert.Equal(14, catalog.Find("lemans 24h", "dallarap217", wet: false)!.Number);
        Assert.Equal(19, catalog.Find("lemans 24h", "dallarap217", wet: true)!.Number);
    }

    [Fact]
    public void ADryChallengeDoesNotMatchInTheWet()
    {
        // Otherwise the wet targets would be handed out in the dry, where they're trivial.
        Assert.Null(ChallengeCatalog.FromJson(TwoRows).Find("limerock full", "mx5 mx52016", wet: true));
    }

    [Fact]
    public void FromJson_RejectsTimesThatAreOutOfOrder()
    {
        const string backwards = """
        [
          { "number": 1, "name": "A", "trackId": "t", "carId": "c",
            "gold": "1:05.000", "silver": "1:01.000", "bronze": "1:02.000" }
        ]
        """;
        Assert.Throws<InvalidDataException>(() => { ChallengeCatalog.FromJson(backwards); });
    }

    [Fact]
    public void Embedded_LoadsAndEveryRowIsWellFormed()
    {
        foreach (var c in ChallengeCatalog.Embedded.Challenges)
        {
            Assert.False(string.IsNullOrWhiteSpace(c.Name), $"Challenge {c.Number} has no name.");
            Assert.False(string.IsNullOrWhiteSpace(c.TrackId), $"Challenge {c.Number} has no trackId.");
            Assert.False(string.IsNullOrWhiteSpace(c.CarId), $"Challenge {c.Number} has no carId.");
            c.Validate();
        }
    }

    [Fact]
    public void Embedded_MarksChallenges16To20AsWet()
    {
        foreach (var c in ChallengeCatalog.Embedded.Challenges)
            Assert.Equal(c.Number >= 16, c.Wet);
    }

    [Fact]
    public void Embedded_NumbersTheChallengesOneToTwenty()
    {
        var numbers = ChallengeCatalog.Embedded.Challenges.Select(c => c.Number).OrderBy(n => n).ToList();

        Assert.True(numbers.SequenceEqual(Enumerable.Range(1, 20)),
            $"challenges.json holds {numbers.Count} of the 20 ARA challenges " +
            $"(numbers: {string.Join(", ", numbers)}). Add the missing rows to " +
            "src/AraOverlay.Core/challenges.json — this test is the reminder.");
    }
}
