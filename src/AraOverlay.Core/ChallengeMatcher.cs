namespace AraOverlay.Core;

/// <summary>
/// Which challenge the live session is: the catalog's matches for the track, car and conditions,
/// and which of them the driver picked. A newly fetched catalog is held back while a challenge is
/// live, since swapping targets mid-lap would grade the lap against rows the driver never saw.
/// </summary>
public sealed class ChallengeMatcher
{
    private ChallengeCatalog _catalog = new([]);
    private ChallengeCatalog? _staged;
    private int _selected;

    /// <summary>Every challenge this track and car could be, one per plan.</summary>
    public IReadOnlyList<Challenge> Matches { get; private set; } = [];

    /// <summary>Which of the matches the panel is showing, or null.</summary>
    public Challenge? Current => _selected < Matches.Count ? Matches[_selected] : null;

    public bool HasChallenges => _catalog.Challenges.Count > 0;

    /// <summary>
    /// Re-runs the lookup, applying a staged catalog if nothing was live. Keeps the driver's plan
    /// pick when that plan still matches, else starts from the first.
    /// </summary>
    /// <param name="track">iRacing track id.</param>
    /// <param name="car">iRacing car id.</param>
    /// <param name="wet">Whether the track is wet.</param>
    public void Match(int track, int car, bool wet)
    {
        if (_staged is { } next && Current is null)
        {
            _catalog = next;
            _staged = null;
        }

        var keep = Current?.Plan;

        Matches = _catalog.Find(track, car, wet);
        _selected = keep is null ? 0 : Math.Max(0, Matches.ToList().FindIndex(c => c.Plan == keep));
    }

    /// <summary>Stages a new catalog, applying it now if no challenge is live.</summary>
    /// <param name="catalog">The catalog to move to.</param>
    /// <param name="track">iRacing track id.</param>
    /// <param name="car">iRacing car id.</param>
    /// <param name="wet">Whether the track is wet.</param>
    /// <returns>True if it was applied now.</returns>
    public bool Swap(ChallengeCatalog catalog, int track, int car, bool wet)
    {
        _staged = catalog;
        if (Current is not null) return false;

        Match(track, car, wet);
        return true;
    }

    /// <summary>Picks a different one of the matches.</summary>
    /// <param name="index">Which match to show.</param>
    /// <returns>False if the index was out of range and ignored.</returns>
    public bool Select(int index)
    {
        if (index < 0 || index >= Matches.Count) return false;

        _selected = index;
        return true;
    }

    /// <summary>Drops the match, on disconnect. The catalog and any staged one are kept.</summary>
    public void Clear()
    {
        Matches = [];
        _selected = 0;
    }
}
