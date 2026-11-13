using AraOverlay.Core;

namespace AraOverlay.Core.Tests;

/// <summary>Covers which challenge the panel grades against as the session and catalog change.</summary>
public class ChallengeMatcherTests
{
    /// <summary>Builds one row for car 2.</summary>
    /// <param name="plan">The plan it belongs to.</param>
    /// <param name="number">Its number.</param>
    /// <param name="bronze">The slowest target in seconds; the others sit a second and two below.</param>
    /// <param name="track">Its track id.</param>
    /// <returns>The challenge.</returns>
    private static Challenge Row(string plan, int number, double bronze = 62, int track = 1) => new()
    {
        Number = number,
        Plan = plan,
        TrackIds = [track],
        CarId = 2,
        Gold = TimeFormat.Format(bronze - 2),
        Silver = TimeFormat.Format(bronze - 1),
        Bronze = TimeFormat.Format(bronze),
    };

    [Fact]
    public void ADriversPlanPickSurvivesAWetFlip()
    {
        var matcher = new ChallengeMatcher();
        matcher.Swap(new ChallengeCatalog([Row("A", 1), Row("B", 7), Row("B", 8, bronze: 90)]), 1, 2, wet: false);
        matcher.Select(1);

        matcher.Match(1, 2, wet: true);

        Assert.Equal(8, matcher.Current!.Number);
    }

    [Fact]
    public void APickThatNoLongerAppliesFallsBackToTheFirst()
    {
        var matcher = new ChallengeMatcher();
        matcher.Swap(new ChallengeCatalog([Row("A", 1), Row("B", 7), Row("A", 3, track: 5)]), 1, 2, wet: false);
        matcher.Select(1);

        matcher.Match(5, 2, wet: false);

        Assert.Equal(3, matcher.Current!.Number);
    }

    [Fact]
    public void ANewCatalogWaitsWhileAChallengeIsLive()
    {
        // Swapping mid-lap would grade the lap against targets the driver never saw.
        var matcher = new ChallengeMatcher();
        matcher.Swap(new ChallengeCatalog([Row("A", 1)]), 1, 2, wet: false);

        Assert.False(matcher.Swap(new ChallengeCatalog([Row("A", 9)]), 1, 2, wet: false));
        matcher.Match(1, 2, wet: true);
        Assert.Equal(1, matcher.Current!.Number);

        matcher.Match(0, 0, wet: false);
        matcher.Match(1, 2, wet: false);
        Assert.Equal(9, matcher.Current!.Number);
    }

    [Fact]
    public void ANewCatalogAppliesAtOnceWhenNothingIsLive()
    {
        var matcher = new ChallengeMatcher();

        Assert.True(matcher.Swap(new ChallengeCatalog([Row("A", 1)]), 1, 2, wet: false));
        Assert.Equal(1, matcher.Current!.Number);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(2)]
    public void AnOutOfRangePickIsIgnored(int index)
    {
        var matcher = new ChallengeMatcher();
        matcher.Swap(new ChallengeCatalog([Row("A", 1), Row("B", 7)]), 1, 2, wet: false);

        Assert.False(matcher.Select(index));
        Assert.Equal(1, matcher.Current!.Number);
    }

    [Fact]
    public void ClearingDropsTheMatchButKeepsTheCatalog()
    {
        var matcher = new ChallengeMatcher();
        matcher.Swap(new ChallengeCatalog([Row("A", 1)]), 1, 2, wet: false);

        matcher.Clear();

        Assert.Null(matcher.Current);
        Assert.True(matcher.HasChallenges);
        matcher.Match(1, 2, wet: false);
        Assert.Equal(1, matcher.Current!.Number);
    }
}
