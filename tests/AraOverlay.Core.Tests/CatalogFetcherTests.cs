using System.Net;
using AraOverlay.Core;

namespace AraOverlay.Core.Tests;

/// <summary>
/// Covers reading the whole catalog. The rule under test throughout is that a refresh either
/// works or says why — a failure must never reach the overlay as a shorter list of challenges.
/// </summary>
public class CatalogFetcherTests
{
    /// <summary>Answers by route, so a listing and its details can fail independently.</summary>
    private sealed class Routes : HttpMessageHandler
    {
        public (HttpStatusCode Status, string Body) List { get; set; } = (HttpStatusCode.OK, "");
        public (HttpStatusCode Status, string Body) Detail { get; set; } = (HttpStatusCode.OK, "");
        public (HttpStatusCode Status, string Body) Session { get; set; } = (HttpStatusCode.OK, "{}");

        public List<string> Requested { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancel)
        {
            var path = request.RequestUri!.PathAndQuery;
            Requested.Add(path);

            var (status, body) =
                path.Contains("/auth/session") ? Session
                : path.Contains("/training-plans?") ? List
                : Detail;

            return Task.FromResult(new HttpResponseMessage(status) { Content = new StringContent(body) });
        }
    }

    private static string Listing(params string[] ids) =>
        $$"""
        { "team": "almeida-racing-academy", "total": {{ids.Length}}, "limit": 100, "offset": 0,
          "items": [{{string.Join(",", ids.Select(i => $"{{\"id\":\"{i}\",\"name\":\"Plan\"}}"))}}] }
        """;

    /// <summary>One target-time content item.</summary>
    private static string Content(
        string contentId, int trackPlatformId,
        double bronze = 108.5, double silver = 108, double gold = 107.5) =>
        $$"""
        { "id": "{{contentId}}", "type": "target_time", "track": 1, "cars": [8],
          "targets": [ { "id": "bronze", "lapTime": {{bronze}} },
                       { "id": "silver", "lapTime": {{silver}} },
                       { "id": "gold",   "lapTime": {{gold}} } ],
          "trackInfo": { "id": 1, "name": "T", "platform": "iracing", "platform_id": "{{trackPlatformId}}" },
          "carInfos": [ { "id": 8, "name": "C", "platform": "iracing", "platform_id": "67" } ],
          "result": null }
        """;

    private static string Detail(string contentId, int trackPlatformId) =>
        $$"""
        { "trainingPlanId": "p", "team": "almeida-racing-academy", "mode": "plan",
          "trainingPlan": { "id": "p", "content": [{{Content(contentId, trackPlatformId)}}] } }
        """;

    private static Task<CatalogFetch> Fetch(Routes routes) =>
        CatalogFetcher.Fetch(new ApiClient(new Uri("https://labs.example"), routes), "ara_token");

    [Fact]
    public async Task ReadsEveryPlanIntoOneCatalog()
    {
        var routes = new Routes { List = (HttpStatusCode.OK, Listing("plan-a")), Detail = (HttpStatusCode.OK, Detail("c1", 166)) };

        var fetch = await Fetch(routes);

        Assert.True(fetch.Usable);
        Assert.Null(fetch.Error);
        Assert.Equal("c1", Assert.Single(fetch.Challenges).ContentId);
    }

    [Fact]
    public async Task AsksForEveryPlanTheListingNamed()
    {
        var routes = new Routes { List = (HttpStatusCode.OK, Listing("plan-a", "plan-b")), Detail = (HttpStatusCode.OK, Detail("c1", 166)) };

        await Fetch(routes);

        Assert.Contains(routes.Requested, p => p.Contains("plan-a"));
        Assert.Contains(routes.Requested, p => p.Contains("plan-b"));
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized, "expired")]
    [InlineData(HttpStatusCode.Forbidden, "Gold")]
    [InlineData(HttpStatusCode.ServiceUnavailable, "paused")]
    public async Task AListingFailureSaysWhyAndKeepsNothing(HttpStatusCode status, string expected)
    {
        var routes = new Routes { List = (status, """{"error":"no"}"""), Session = (status, """{"error":"no"}""") };

        var fetch = await Fetch(routes);

        Assert.False(fetch.Usable);
        Assert.Contains(expected, fetch.Error!, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(fetch.Challenges);
    }

    [Fact]
    public async Task OnePlanFailingLosesTheWholeRefreshRatherThanSomeChallenges()
    {
        // A short list would look exactly like a challenge being retired.
        var routes = new Routes
        {
            List = (HttpStatusCode.OK, Listing("plan-a", "plan-b")),
            Detail = (HttpStatusCode.BadGateway, """{"error":"upstream"}"""),
        };

        var fetch = await Fetch(routes);

        Assert.False(fetch.Usable);
        Assert.NotNull(fetch.Error);
        Assert.Empty(fetch.Challenges);
    }

    [Fact]
    public async Task AnUpstreamFailureNeverAsksTheMemberToSignInAgain()
    {
        // 502 is Garage61's problem, not the member's session.
        var routes = new Routes { List = (HttpStatusCode.OK, Listing("plan-a")), Detail = (HttpStatusCode.BadGateway, "{}") };

        var fetch = await Fetch(routes);

        Assert.DoesNotContain("sign in", fetch.Error!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task AnEmptyCatalogIsNotUsable()
    {
        // Better to keep whatever is cached than to show a driver no challenges at all.
        var routes = new Routes { List = (HttpStatusCode.OK, Listing()), Detail = (HttpStatusCode.OK, "{}") };

        var fetch = await Fetch(routes);

        Assert.False(fetch.Usable);
        Assert.Empty(fetch.Challenges);
    }

    [Fact]
    public async Task UnreadableJsonIsAFailureNotAnEmptyCatalog()
    {
        var routes = new Routes { List = (HttpStatusCode.OK, "<html>nope</html>") };

        var fetch = await Fetch(routes);

        Assert.False(fetch.Usable);
        Assert.NotNull(fetch.Error);
    }

    [Fact]
    public async Task SkippedItemsAreCarriedBackWithoutFailingTheRefresh()
    {
        var unmapped = Detail("c1", 166).Replace("\"platform\": \"iracing\", \"platform_id\": \"166\"", "\"platform\": \"acc\"");
        var routes = new Routes { List = (HttpStatusCode.OK, Listing("plan-a")), Detail = (HttpStatusCode.OK, unmapped) };

        var fetch = await Fetch(routes);

        Assert.Single(fetch.Skipped);
        Assert.Null(fetch.Error);
        Assert.False(fetch.Usable);
    }

    [Fact]
    public async Task StopsRatherThanLoopingWhenTheServerIgnoresTheOffset()
    {
        // total says more than a page, but every page returns the same item.
        var stuck = """
            { "team": "t", "total": 500, "limit": 100, "offset": 0,
              "items": [{"id":"plan-a","name":"Plan"}] }
            """;

        var routes = new Routes { List = (HttpStatusCode.OK, stuck), Detail = (HttpStatusCode.OK, Detail("c1", 166)) };

        var fetch = await Fetch(routes);

        Assert.True(fetch.Usable);
        Assert.InRange(routes.Requested.Count(p => p.Contains("training-plans?")), 1, 3);
    }

    [Fact]
    public async Task AChallengeInTwoPlansIsOneChallenge()
    {
        // A plan set holds regular and weekly challenges and the same one can appear in both.
        var routes = new Routes
        {
            List = (HttpStatusCode.OK, Listing("regular", "weekly")),
            Detail = (HttpStatusCode.OK, Detail("c1", 166)),
        };

        var fetch = await Fetch(routes);

        Assert.True(fetch.Usable);
        Assert.Equal("c1", Assert.Single(fetch.Challenges).ContentId);
        Assert.Single(fetch.Skipped);
    }

    [Fact]
    public async Task TwoChallengesGradingIdenticallyDoNotCostTheWholeCatalog()
    {
        // Same track, car and targets: matching either grades a lap the same, so keeping the
        // first is harmless where rejecting everything would leave the driver with nothing.
        var routes = new Routes
        {
            List = (HttpStatusCode.OK, Listing("plan-a")),
            Detail = (HttpStatusCode.OK, $$"""
                { "trainingPlanId": "p", "team": "t", "mode": "plan", "trainingPlan": { "id": "p", "content": [
                  {{Content("c1", 166)}}, {{Content("c8", 166)}} ] } }
                """),
        };

        var fetch = await Fetch(routes);

        Assert.True(fetch.Usable);
        Assert.Equal("c1", Assert.Single(fetch.Challenges).ContentId);
        Assert.Contains(fetch.Skipped, s => s.Contains("c8"));
    }

    [Fact]
    public async Task AWetAndDryPairIsStillKept()
    {
        // Le Mans: the same track and car, told apart by the wet one's targets being slower.
        var routes = new Routes
        {
            List = (HttpStatusCode.OK, Listing("plan-a")),
            Detail = (HttpStatusCode.OK, $$"""
                { "trainingPlanId": "p", "team": "t", "mode": "plan", "trainingPlan": { "id": "p", "content": [
                  {{Content("dry", 268)}}, {{Content("wet", 268, bronze: 253.7, silver: 251.2, gold: 250.2)}} ] } }
                """),
        };

        var fetch = await Fetch(routes);

        Assert.Equal(2, fetch.Challenges.Count);
        Assert.Empty(fetch.Skipped);

        var catalog = ChallengeCatalog.FromChallenges(fetch.Challenges);
        Assert.Equal("dry", Assert.Single(catalog.Find(268, 67, wet: false)).ContentId);
        Assert.Equal("wet", Assert.Single(catalog.Find(268, 67, wet: true)).ContentId);
    }

    [Fact]
    public async Task AFetchedCatalogFeedsTheLookupStraightBack()
    {
        var routes = new Routes { List = (HttpStatusCode.OK, Listing("plan-a")), Detail = (HttpStatusCode.OK, Detail("c1", 166)) };

        var catalog = ChallengeCatalog.FromChallenges((await Fetch(routes)).Challenges);

        Assert.Equal("c1", Assert.Single(catalog.Find(166, 67, wet: false)).ContentId);
    }
}
