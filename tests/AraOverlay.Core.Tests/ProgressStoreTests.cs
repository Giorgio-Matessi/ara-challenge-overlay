using AraOverlay.Core;

namespace AraOverlay.Core.Tests;

/// <summary>Covers stored progress and when a lap counts as an improvement.</summary>
public class ProgressStoreTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "ara-tests-" + Guid.NewGuid().ToString("N"));

    private string Path_ => Path.Combine(_dir, "progress.json");

    public void Dispose()
    {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
    }

    [Fact]
    public void FirstMedalIsWorthAPopup()
    {
        var store = new ProgressStore(Path_);
        Assert.True(store.RecordLap("local:1", 54.9, Medal.Bronze));
    }

    [Fact]
    public void RepeatingTheSameTierIsNot()
    {
        var store = new ProgressStore(Path_);
        store.RecordLap("local:1", 54.9, Medal.Bronze);
        Assert.False(store.RecordLap("local:1", 54.8, Medal.Bronze));
        Assert.False(store.RecordLap("local:1", 54.95, Medal.Bronze));
    }

    [Fact]
    public void ImprovingTheTierIs()
    {
        var store = new ProgressStore(Path_);
        store.RecordLap("local:1", 54.9, Medal.Bronze);
        Assert.True(store.RecordLap("local:1", 54.1, Medal.Silver));
        Assert.True(store.RecordLap("local:1", 53.4, Medal.Gold));
        Assert.False(store.RecordLap("local:1", 53.1, Medal.Gold));
    }

    [Fact]
    public void DroppingBackToASlowerLapDoesNotLoseTheMedal()
    {
        var store = new ProgressStore(Path_);
        store.RecordLap("local:1", 53.4, Medal.Gold);
        Assert.False(store.RecordLap("local:1", 58.0, Medal.None));
        Assert.Equal(Medal.Gold, store.Get("local:1")!.BestMedal);
        Assert.Equal(53.4, store.Get("local:1")!.BestSeconds, 3);
    }

    [Fact]
    public void BestTimeTracksTheFastestLapEvenWithoutAMedal()
    {
        var store = new ProgressStore(Path_);
        Assert.False(store.RecordLap("local:1", 61.0, Medal.None));
        Assert.False(store.RecordLap("local:1", 59.5, Medal.None));
        Assert.Equal(59.5, store.Get("local:1")!.BestSeconds, 3);
        Assert.Equal(Medal.None, store.Get("local:1")!.BestMedal);
    }

    [Fact]
    public void ChallengesAreTrackedIndependently()
    {
        var store = new ProgressStore(Path_);
        store.RecordLap("local:1", 54.9, Medal.Bronze);
        Assert.True(store.RecordLap("local:2", 120.0, Medal.Bronze));
        Assert.Null(store.Get("local:3"));
    }

    [Fact]
    public void ProgressSurvivesARestart()
    {
        new ProgressStore(Path_).RecordLap("local:7", 54.1, Medal.Silver);

        var reloaded = new ProgressStore(Path_);
        Assert.Equal(Medal.Silver, reloaded.Get("local:7")!.BestMedal);
        Assert.Equal(54.1, reloaded.Get("local:7")!.BestSeconds, 3);
        Assert.False(reloaded.RecordLap("local:7", 54.0, Medal.Silver));
    }

    [Fact]
    public void MissingFileLoadsAsEmpty()
    {
        var store = new ProgressStore(Path.Combine(_dir, "nested", "nope.json"));
        Assert.Null(store.Get("local:1"));
    }

    [Fact]
    public void ProgressFromBeforeTheApiIsIgnoredRatherThanMisread()
    {
        // v0.0.4 and earlier keyed on the challenge number. Those records stay in the file and
        // stay inert: the server restores real history once Garage61 is linked.
        Directory.CreateDirectory(_dir);
        File.WriteAllText(Path_, """{ "14": { "BestSeconds": 216.4, "BestMedal": 3 } }""");

        var store = new ProgressStore(Path_);
        Assert.Null(store.Get("local:14"));
        Assert.True(store.RecordLap("local:14", 216.4, Medal.Gold));
    }

    [Fact]
    public void CorruptFileLoadsAsEmptyRatherThanThrowing()
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(Path_, "{ this is not json");

        var store = new ProgressStore(Path_);
        Assert.Null(store.Get("local:1"));
        Assert.True(store.RecordLap("local:1", 54.9, Medal.Bronze));
    }
}
