using AraOverlay.Core;

namespace AraOverlay.Core.Tests;

/// <summary>Covers how long to wait after a rate limit or a temporary failure.</summary>
public class RetryPolicyTests
{
    [Theory]
    [InlineData("30", 30)]
    [InlineData("5", 5)]
    [InlineData("120", 120)]
    [InlineData("  45  ", 45)]
    public void RetryAfterIsTakenFromTheHeader(string header, int expected)
    {
        Assert.Equal(TimeSpan.FromSeconds(expected), RetryPolicy.RetryAfter(header));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("Wed, 21 Oct 2026 07:28:00 GMT")]
    [InlineData("soon")]
    [InlineData("99999999999999999999")]
    public void UnreadableRetryAfterWaitsAMinute(string? header)
    {
        // The docs say a minute when it's missing, and an edge response may not carry a usable
        // one at all. Guessing shorter is how a rate limit turns into a ban.
        Assert.Equal(TimeSpan.FromMinutes(1), RetryPolicy.RetryAfter(header));
    }

    [Theory]
    [InlineData("0")]
    [InlineData("1")]
    [InlineData("-10")]
    public void RetryAfterNeverDropsBelowThePollingInterval(string header)
    {
        // The token route refuses faster than one poll every five seconds.
        Assert.Equal(TimeSpan.FromSeconds(5), RetryPolicy.RetryAfter(header));
    }

    [Theory]
    [InlineData(1, 10)]
    [InlineData(2, 20)]
    [InlineData(3, 40)]
    [InlineData(4, 60)]
    [InlineData(9, 60)]
    public void BackoffDoublesAndThenStops(int attempt, int floor)
    {
        var wait = RetryPolicy.Backoff(attempt);

        Assert.InRange(wait.TotalSeconds, floor, floor + 1);
    }

    [Fact]
    public void BackoffIsJitteredSoClientsDoNotReturnTogether()
    {
        var waits = Enumerable.Range(0, 50).Select(_ => RetryPolicy.Backoff(1).TotalSeconds).ToList();

        Assert.True(waits.Distinct().Count() > 1, "Backoff produced the same delay every time.");
    }

    [Theory]
    [InlineData(502, true)]
    [InlineData(503, true)]
    [InlineData(504, true)]
    [InlineData(500, true)]
    [InlineData(400, false)]
    [InlineData(401, false)]
    [InlineData(403, false)]
    [InlineData(404, false)]
    [InlineData(409, false)]
    [InlineData(410, false)]
    [InlineData(429, false)]
    public void OnlyServerSideFailuresAreWorthRetrying(int status, bool expected)
    {
        // 429 has its own wait, and everything in the 4xx range needs the member to do something.
        Assert.Equal(expected, RetryPolicy.IsTransient(status));
    }
}
