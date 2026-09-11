namespace AraOverlay.Core;

public sealed record ChallengeProgress(double BestSeconds, Medal BestMedal);

/// <summary>
/// Best lap and best medal per challenge, persisted as JSON.
///
/// This exists mainly to keep the popup honest: once you hold gold, every subsequent gold lap
/// must not re-pop the banner. <see cref="RecordLap"/> returns true only when the tier improves.
/// </summary>
public sealed class ProgressStore
{
    private readonly string _path;
    private readonly Dictionary<int, ChallengeProgress> _progress;

    public ProgressStore(string path)
    {
        _path = path;
        _progress = JsonFile.Load<Dictionary<int, ChallengeProgress>>(path) ?? new();
    }

    public ChallengeProgress? Get(int challengeNumber) => _progress.GetValueOrDefault(challengeNumber);

    /// <summary>
    /// Records a completed clean lap. Returns true if this lap earned a better medal than the
    /// driver already held for this challenge — i.e. whether it's worth a popup.
    /// </summary>
    public bool RecordLap(int challengeNumber, double seconds, Medal medal)
    {
        var previous = Get(challengeNumber);

        var bestSeconds = previous is null ? seconds : Math.Min(previous.BestSeconds, seconds);
        var bestMedal = previous is null ? medal : (Medal)Math.Max((int)medal, (int)previous.BestMedal);
        var improvedTier = bestMedal > (previous?.BestMedal ?? Medal.None);

        var updated = new ChallengeProgress(bestSeconds, bestMedal);
        if (updated != previous)
        {
            _progress[challengeNumber] = updated;
            Save();
        }

        return improvedTier;
    }

    private void Save() => JsonFile.Save(_path, _progress);
}
