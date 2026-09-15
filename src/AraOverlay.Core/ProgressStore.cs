namespace AraOverlay.Core;

public sealed record ChallengeProgress(double BestSeconds, Medal BestMedal);

/// <summary>
/// Best lap and best medal per challenge, persisted as JSON. Exists mainly to keep the banner
/// honest: once a driver holds gold, no later gold lap may re-pop it.
/// </summary>
public sealed class ProgressStore
{
    private readonly string _path;
    private readonly Dictionary<int, ChallengeProgress> _progress;

    /// <summary>Loads the stored progress, or starts empty if there's no usable file.</summary>
    /// <param name="path">Where the JSON lives.</param>
    public ProgressStore(string path)
    {
        _path = path;
        _progress = JsonFile.Load<Dictionary<int, ChallengeProgress>>(path) ?? new();
    }

    /// <summary>Reads one challenge's record.</summary>
    /// <param name="challengeNumber">The challenge number, 1 to 20.</param>
    /// <returns>The stored best, or null if nothing is recorded.</returns>
    public ChallengeProgress? Get(int challengeNumber) => _progress.GetValueOrDefault(challengeNumber);

    /// <summary>Records a completed clean lap, saving if anything changed.</summary>
    /// <param name="challengeNumber">The challenge the lap was set on.</param>
    /// <param name="seconds">The lap time.</param>
    /// <param name="medal">The medal it earned.</param>
    /// <returns>True only if it beat the medal already held.</returns>
    public bool RecordLap(int challengeNumber, double seconds, Medal medal)
    {
        var previous = Get(challengeNumber);
        var heldMedal = previous?.BestMedal ?? Medal.None;

        var updated = new ChallengeProgress(
            Math.Min(previous?.BestSeconds ?? seconds, seconds),
            medal > heldMedal ? medal : heldMedal);

        if (updated != previous)
        {
            _progress[challengeNumber] = updated;
            Save();
        }

        return updated.BestMedal > heldMedal;
    }

    /// <summary>Writes the store to disk. A failed write is dropped.</summary>
    private void Save() => JsonFile.Save(_path, _progress);
}
