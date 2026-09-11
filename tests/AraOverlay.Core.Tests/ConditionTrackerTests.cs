using AraOverlay.Core;

namespace AraOverlay.Core.Tests;

public class ConditionTrackerTests
{
    [Fact]
    public void StartsDry()
    {
        Assert.False(new ConditionTracker().IsWet);
    }

    [Theory]
    [InlineData(TrackWetness.Dry)]
    [InlineData(TrackWetness.MostlyDry)]
    [InlineData(TrackWetness.VeryLightlyWet)]   // below the threshold to declare a wet session
    public void StaysDryUntilItIsProperlyWet(int wetness)
    {
        var conditions = new ConditionTracker();
        Assert.False(conditions.Update(wetness, declaredWet: false));
        Assert.False(conditions.IsWet);
    }

    [Theory]
    [InlineData(TrackWetness.LightlyWet)]
    [InlineData(TrackWetness.ModeratelyWet)]
    [InlineData(TrackWetness.VeryWet)]
    [InlineData(TrackWetness.ExtremelyWet)]
    public void TurnsWetOnceTheSurfaceIsWetEnough(int wetness)
    {
        var conditions = new ConditionTracker();
        Assert.True(conditions.Update(wetness, declaredWet: false));
        Assert.True(conditions.IsWet);
    }

    [Fact]
    public void MarshalsDeclaringWetIsEnoughOnItsOwn()
    {
        var conditions = new ConditionTracker();
        Assert.True(conditions.Update(TrackWetness.Dry, declaredWet: true));
        Assert.True(conditions.IsWet);
    }

    [Fact]
    public void ADryingTrackHoldsWetThroughTheMiddleGround()
    {
        var conditions = new ConditionTracker();
        conditions.Update(TrackWetness.VeryWet, declaredWet: false);

        // Hysteresis: coming down, VeryLightlyWet is not yet dry, so a drying track
        // can't flip the challenge back and forth around a single threshold.
        Assert.False(conditions.Update(TrackWetness.VeryLightlyWet, declaredWet: false));
        Assert.True(conditions.IsWet);

        Assert.True(conditions.Update(TrackWetness.MostlyDry, declaredWet: false));
        Assert.False(conditions.IsWet);
    }

    [Fact]
    public void StaysWetWhileRainTyresAreStillAllowed()
    {
        var conditions = new ConditionTracker();
        conditions.Update(TrackWetness.VeryWet, declaredWet: true);

        Assert.False(conditions.Update(TrackWetness.Dry, declaredWet: true));
        Assert.True(conditions.IsWet);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void UnknownWetnessIsTreatedAsNoInformation(bool startWet)
    {
        var conditions = new ConditionTracker();
        if (startWet) conditions.Update(TrackWetness.VeryWet, declaredWet: false);

        Assert.False(conditions.Update(TrackWetness.Unknown, declaredWet: false));
        Assert.Equal(startWet, conditions.IsWet);
    }

    [Fact]
    public void UpdateReportsChangeOnlyOnTheFrameItFlips()
    {
        var conditions = new ConditionTracker();
        Assert.True(conditions.Update(TrackWetness.VeryWet, declaredWet: false));
        Assert.False(conditions.Update(TrackWetness.VeryWet, declaredWet: false));
        Assert.False(conditions.Update(TrackWetness.ExtremelyWet, declaredWet: false));
    }

    [Fact]
    public void ResetReturnsToDry()
    {
        var conditions = new ConditionTracker();
        conditions.Update(TrackWetness.VeryWet, declaredWet: true);
        conditions.Reset();
        Assert.False(conditions.IsWet);
    }
}
