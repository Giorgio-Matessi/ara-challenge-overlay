using AraOverlay.Core;

namespace AraOverlay.Core.Tests;

public class ChallengeCatalogTests
{
    private const string TwoRows = """
    [
      { "number": 1, "track": "A", "car": "X", "trackIds": [299], "carId": 142,
        "gold": "0:53.500", "silver": "0:54.200", "bronze": "0:55.000" },
      { "number": 2, "track": "B", "car": "X", "trackIds": [181], "carId": 67,
        "gold": "2:30.000", "silver": "2:32.000", "bronze": "2:34.000" }
    ]
    """;

    [Fact]
    public void Find_MatchesOnTrackAndCar()
    {
        var catalog = ChallengeCatalog.FromJson(TwoRows);
        Assert.Equal(1, catalog.Find(299, 142, wet: false)!.Number);
        Assert.Equal(2, catalog.Find(181, 67, wet: false)!.Number);
    }

    [Theory]
    [InlineData(299, 67)]    // right track, wrong car
    [InlineData(181, 142)]   // right car, wrong track
    [InlineData(500, 142)]
    [InlineData(0, 0)]       // the SDK's "nothing loaded yet"
    [InlineData(-1, -1)]
    public void Find_ReturnsNullWhenNothingMatches(int track, int car)
    {
        Assert.Null(ChallengeCatalog.FromJson(TwoRows).Find(track, car, wet: false));
    }

    [Fact]
    public void FromJson_RejectsDuplicatesWithTheSameConditions()
    {
        const string duplicated = """
        [
          { "number": 1, "track": "A", "car": "X", "trackIds": [1], "carId": 2,
            "gold": "1:00.000", "silver": "1:01.000", "bronze": "1:02.000" },
          { "number": 2, "track": "B", "car": "X", "trackIds": [1], "carId": 2,
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
          { "number": 14, "track": "Le Mans dry", "car": "X", "trackIds": [268], "carId": 128,
            "gold": "3:36.250", "silver": "3:37.000", "bronze": "3:38.800" },
          { "number": 19, "track": "Le Mans wet", "car": "X", "trackIds": [268], "carId": 128,
            "wet": true,
            "gold": "4:10.200", "silver": "4:11.200", "bronze": "4:13.700" }
        ]
        """;

        var catalog = ChallengeCatalog.FromJson(lemans);
        Assert.Equal(14, catalog.Find(268, 128, wet: false)!.Number);
        Assert.Equal(19, catalog.Find(268, 128, wet: true)!.Number);
    }

    [Fact]
    public void ADryChallengeDoesNotMatchInTheWet()
    {
        // Otherwise the wet targets would be handed out in the dry, where they're trivial.
        Assert.Null(ChallengeCatalog.FromJson(TwoRows).Find(299, 142, wet: true));
    }

    [Fact]
    public void AChallengeCanBeListedOnSeveralBuildsOfTheSameCircuit()
    {
        // This is challenges 11 and 18: the league counts either Spa layout.
        const string spa = """
        [
          { "number": 11, "track": "Spa", "car": "X", "trackIds": [523, 163], "carId": 128,
            "gold": "2:07.900", "silver": "2:08.700", "bronze": "2:09.700" }
        ]
        """;

        var catalog = ChallengeCatalog.FromJson(spa);
        Assert.Equal(11, catalog.Find(523, 128, wet: false)!.Number);
        Assert.Equal(11, catalog.Find(163, 128, wet: false)!.Number);
        Assert.Null(catalog.Find(268, 128, wet: false));
    }

    [Fact]
    public void FromJson_RejectsARowWithNoTrackIds()
    {
        const string none = """
        [
          { "number": 1, "track": "A", "car": "X", "trackIds": [], "carId": 2,
            "gold": "1:00.000", "silver": "1:01.000", "bronze": "1:02.000" }
        ]
        """;
        Assert.Throws<InvalidDataException>(() => { ChallengeCatalog.FromJson(none); });
    }

    [Fact]
    public void Embedded_NamesTheTrackAndCarSeparatelyForTheOverlayHeader()
    {
        // The overlay puts them on their own lines, so neither may carry the other.
        var c = ChallengeCatalog.Embedded.Challenges.Single(c => c.Number == 13);
        Assert.Equal("Sebring International Raceway (International)", c.Track);
        Assert.Equal("NASCAR Truck Chevrolet Silverado - 2008", c.Car);
    }

    [Fact]
    public void FromJson_RejectsTimesThatAreOutOfOrder()
    {
        const string backwards = """
        [
          { "number": 1, "track": "A", "car": "X", "trackIds": [1], "carId": 2,
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
            Assert.False(string.IsNullOrWhiteSpace(c.Track), $"Challenge {c.Number} has no track.");
            Assert.False(string.IsNullOrWhiteSpace(c.Car), $"Challenge {c.Number} has no car.");
            Assert.NotEmpty(c.TrackIds);
            Assert.True(c.CarId > 0, $"Challenge {c.Number} has no carId.");
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
