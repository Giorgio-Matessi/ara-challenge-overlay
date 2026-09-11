using System.Text.Json;

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
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    private readonly string _path;
    private readonly Dictionary<int, ChallengeProgress> _progress;

    public ProgressStore(string path)
    {
        _path = path;
        _progress = Load(path);
    }

    public static string DefaultPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "AraOverlay",
        "progress.json");

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

    private static Dictionary<int, ChallengeProgress> Load(string path)
    {
        try
        {
            if (!File.Exists(path)) return new Dictionary<int, ChallengeProgress>();
            return JsonSerializer.Deserialize<Dictionary<int, ChallengeProgress>>(File.ReadAllText(path))
                   ?? new Dictionary<int, ChallengeProgress>();
        }
        catch (Exception e) when (e is JsonException or IOException or UnauthorizedAccessException)
        {
            // A corrupt or unreadable file must never stop the overlay from running; the next
            // clean lap rewrites it.
            return new Dictionary<int, ChallengeProgress>();
        }
    }

    private void Save()
    {
        try
        {
            var directory = Path.GetDirectoryName(_path);
            if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
            File.WriteAllText(_path, JsonSerializer.Serialize(_progress, JsonOptions));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // Losing progress is annoying; crashing mid-session is worse.
        }
    }
}
