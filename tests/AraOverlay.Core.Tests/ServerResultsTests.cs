using AraOverlay.Core;

namespace AraOverlay.Core.Tests;

/// <summary>
/// Covers reading the member's Garage61 bests. The trap under test is targetReached being
/// tri-state: null is "unknown" and must never be read as "no medal".
/// </summary>
public class ServerResultsTests
{
    private static string Results(string best) =>
        $$"""
        { "trainingPlanId": "p", "team": "almeida-racing-academy", "fetchedAt": "2026-11-10T12:00:00Z",
          "items": [ { "contentId": "c1", "best": {{best}} } ] }
        """;

    private static string Best(string targetReached) =>
        $$"""
        { "lapId": "l", "eventId": null, "lapTime": 49.5, "drivenAt": "2026-09-20T12:00:00Z", "targetReached": {{targetReached}} }
        """;

    [Theory]
    [InlineData("\"silver\"", Medal.Silver)]
    [InlineData("\"GOLD\"", Medal.Gold)]
    [InlineData("\"bronze\"", Medal.Bronze)]
    [InlineData("\"\"", Medal.None)]
    public void ReadsTheServersMedal(string targetReached, Medal expected)
    {
        var best = Assert.Single(ServerResults.Parse(Results(Best(targetReached)))!);

        Assert.Equal(new ServerBest("c1", 49.5, expected), best);
    }

    [Theory]
    [InlineData("null")]
    [InlineData("\"platinum\"")]
    public void AnUnknownVerdictLeavesLocalProgressAlone(string targetReached)
    {
        // Reading null as "" would overwrite a real medal with a false zero.
        Assert.Empty(ServerResults.Parse(Results(Best(targetReached)))!);
    }

    [Fact]
    public void AChallengeWithNoRecordedLapIsSkipped()
    {
        Assert.Empty(ServerResults.Parse(Results("null"))!);
    }

    [Theory]
    [InlineData("<html>nope</html>")]
    [InlineData("[]")]
    [InlineData("{}")]
    public void AnUnreadableBodyIsNullNotEmpty(string body)
    {
        // Empty would be merged as "nothing to restore"; null says the read failed.
        Assert.Null(ServerResults.Parse(body));
    }

    [Theory]
    [InlineData("G61_NOT_LINKED", "Link Garage61")]
    [InlineData("G61_RECONNECT_REQUIRED", "Reconnect Garage61")]
    [InlineData("G61_IDENTITY_MISMATCH", "different")]
    [InlineData("SOMETHING_NEW", "Garage61")]
    public void EachLinkCodeGetsItsOwnHint(string code, string expected)
    {
        var body = $$"""{ "error": "x", "code": "{{code}}", "message": "m", "linkUrl": "https://labs.example/link", "requestId": "r" }""";

        var (hint, url) = ServerResults.LinkHint(body);

        Assert.Contains(expected, hint);
        Assert.DoesNotContain("sign in", hint, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("https://labs.example/link", url);
    }

    [Theory]
    [InlineData("""{ "code": "G61_NOT_LINKED", "linkUrl": "javascript:alert(1)" }""")]
    [InlineData("""{ "code": "G61_NOT_LINKED", "linkUrl": "http://labs.example/link" }""")]
    [InlineData("not json")]
    public void OnlyAnHttpsLinkIsOffered(string body)
    {
        var (hint, url) = ServerResults.LinkHint(body);

        Assert.NotEmpty(hint);
        Assert.Null(url);
    }
}
