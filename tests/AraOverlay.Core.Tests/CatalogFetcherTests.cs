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

    private static string Detail(string contentId, int trackPlatformId) =>
        $$"""
        { "trainingPlanId": "p", "team": "almeida-racing-academy", "mode": "plan",
          "trainingPlan": { "id": "p", "content": [
            { "id": "{{contentId}}", "type": "target_time", "track": 1, "cars": [8],
              "targets": [ { "id": "bronze", "lapTime": 108.5 },
                           { "id": "silver", "lapTime": 108 },
                           { "id": "gold",   "lapTime": 107.5 } ],
              "trackInfo": { "id": 1, "name": "T", "platform": "iracing", "platform_id": "{{trackPlatformId}}" },
              "carInfos": [ { "id": 8, "name": "C", "platform": "iracing", "platform_id": "67" } ],
              "result": null } ] } }
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
    public async Task AFetchedCatalogFeedsTheLookupStraightBack()
    {
        var routes = new Routes { List = (HttpStatusCode.OK, Listing("plan-a")), Detail = (HttpStatusCode.OK, Detail("c1", 166)) };

        var catalog = ChallengeCatalog.FromChallenges((await Fetch(routes)).Challenges);

        Assert.Equal("c1", catalog.Find(166, 67, wet: false)!.ContentId);
    }
}
