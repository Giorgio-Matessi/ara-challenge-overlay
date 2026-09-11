using AraOverlay.Core;

namespace AraOverlay.Core.Tests;

public class MedalTests
{
    private static readonly Challenge Sample = new()
    {
        Number = 1,
        Name = "Test Challenge",
        TrackIds = [299],
        CarId = 142,
        Gold = "0:53.500",
        Silver = "0:54.200",
        Bronze = "0:55.000",
    };

    [Theory]
    [InlineData(52.000, Medal.Gold)]
    [InlineData(53.499, Medal.Gold)]
    [InlineData(53.501, Medal.Silver)]
    [InlineData(54.199, Medal.Silver)]
    [InlineData(54.201, Medal.Bronze)]
    [InlineData(54.999, Medal.Bronze)]
    [InlineData(55.001, Medal.None)]
    [InlineData(90.000, Medal.None)]
    public void MedalFor_PicksTheRightTier(double lap, Medal expected)
    {
        Assert.Equal(expected, Sample.MedalFor(lap));
    }

    [Theory]
    [InlineData(53.500, Medal.Gold)]
    [InlineData(54.200, Medal.Silver)]
    [InlineData(55.000, Medal.Bronze)]
    public void MedalFor_AcceptsAnExactlyEqualLap(double lap, Medal expected)
    {
        Assert.Equal(expected, Sample.MedalFor(lap));
    }

    [Fact]
    public void MedalFor_AcceptsALapThatOnlyDisplaysAsEqual()
    {
        // iRacing hands us a float; 53.5004 shows as "53.500" on screen, so it counts.
        Assert.Equal(Medal.Gold, Sample.MedalFor(53.5004));
        Assert.Equal(Medal.Silver, Sample.MedalFor(53.5006));
    }

    [Theory]
    [InlineData(-1.0)]   // the SDK's "no lap yet" sentinel
    [InlineData(0.0)]
    public void MedalFor_RejectsNonLaps(double lap)
    {
        Assert.Equal(Medal.None, Sample.MedalFor(lap));
    }

    [Fact]
    public void TargetFor_ReturnsTheThresholdSeconds()
    {
        Assert.Equal(53.5, Sample.TargetFor(Medal.Gold), 3);
        Assert.Equal(54.2, Sample.TargetFor(Medal.Silver), 3);
        Assert.Equal(55.0, Sample.TargetFor(Medal.Bronze), 3);
    }

    [Fact]
    public void NextTierAbove_WalksUpwardsAndStopsAtGold()
    {
        Assert.Equal(Medal.Bronze, Challenge.NextTierAbove(Medal.None));
        Assert.Equal(Medal.Silver, Challenge.NextTierAbove(Medal.Bronze));
        Assert.Equal(Medal.Gold, Challenge.NextTierAbove(Medal.Silver));
        Assert.Null(Challenge.NextTierAbove(Medal.Gold));
    }
}
