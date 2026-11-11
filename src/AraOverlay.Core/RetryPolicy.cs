using System.Globalization;
using System.Security.Cryptography;

namespace AraOverlay.Core;

/// <summary>
/// How long to wait before trying an ARA request again. Rate limits carry their own answer;
/// temporary failures get a doubling wait with jitter, so a hundred overlays coming back from an
/// outage don't arrive together.
/// </summary>
public static class RetryPolicy
{
    private static readonly TimeSpan Shortest = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan Longest = TimeSpan.FromSeconds(60);

    /// <summary>Reads a 429's Retry-After.</summary>
    /// <param name="header">The header value; null when absent, and not always a number.</param>
    /// <returns>How long to wait: the header's seconds, a minute if it can't be read.</returns>
    public static TimeSpan RetryAfter(string? header) =>
        int.TryParse(header, NumberStyles.Integer, CultureInfo.InvariantCulture, out var seconds)
            ? TimeSpan.FromSeconds(Math.Clamp(seconds, Shortest.TotalSeconds, Longest.TotalSeconds * 60))
            : Longest;

    /// <summary>How long to wait after a temporary failure.</summary>
    /// <param name="attempt">Which retry this is, counting from one.</param>
    /// <returns>Five seconds doubled per attempt, capped at a minute, plus up to a second.</returns>
    public static TimeSpan Backoff(int attempt) =>
        TimeSpan.FromSeconds(Math.Min(Longest.TotalSeconds, 5 * Math.Pow(2, Math.Max(1, attempt))))
        + TimeSpan.FromMilliseconds(RandomNumberGenerator.GetInt32(1000));

    /// <summary>Decides whether a status is worth trying again unattended.</summary>
    /// <param name="status">The HTTP status code.</param>
    /// <returns>True for a server-side failure; false for a rate limit or anything in the 4xx range.</returns>
    public static bool IsTransient(int status) => status >= 500;
}
