using System.Net;
using AraOverlay.Core;

namespace AraOverlay.Core.Tests;

/// <summary>
/// Covers what the client sends and what it hands back. No network: a stub handler records the
/// request and replies with whatever the case needs.
/// </summary>
public class ApiClientTests
{
    /// <summary>Records the request it was given and answers with a canned response.</summary>
    private sealed class Stub(HttpStatusCode status, string body, string? retryAfter = null) : HttpMessageHandler
    {
        public HttpRequestMessage? Seen { get; private set; }

        /// <summary>The request body, read here because the client disposes the request it sent.</summary>
        public string SentBody { get; private set; } = "";

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancel)
        {
            Seen = request;
            if (request.Content is not null) SentBody = await request.Content.ReadAsStringAsync(cancel);

            var response = new HttpResponseMessage(status) { Content = new StringContent(body) };
            if (retryAfter is not null) response.Headers.TryAddWithoutValidation("Retry-After", retryAfter);

            return response;
        }
    }

    private static ApiClient Client(Stub stub) => new(new Uri("https://labs.example"), stub);

    [Fact]
    public async Task TheTokenTravelsInTheAuthorizationHeaderAndNowhereElse()
    {
        var stub = new Stub(HttpStatusCode.OK, "{}");
        await Client(stub).GetPlan("ara_secret", "01KCRX69A5AYWJ5AXFXAT9DJNW");

        Assert.Equal("Bearer", stub.Seen!.Headers.Authorization!.Scheme);
        Assert.Equal("ara_secret", stub.Seen.Headers.Authorization.Parameter);
        Assert.DoesNotContain("ara_secret", stub.Seen.RequestUri!.ToString());
    }

    [Fact]
    public async Task TheLoginRoutesSendNoCredentials()
    {
        // There is no client secret to send, and sending a stale bearer would be a way to leak one.
        var stub = new Stub(HttpStatusCode.Created, "{}");
        await Client(stub).StartLogin("E9Melhoa2OwvFrEMTJguCHaoeK1t8URWbuGJSstw-cM");

        Assert.Null(stub.Seen!.Headers.Authorization);
    }

    [Fact]
    public async Task TheLoginBodyCarriesThePublicClientIdAndTheS256Challenge()
    {
        var stub = new Stub(HttpStatusCode.Created, "{}");
        await Client(stub).StartLogin("E9Melhoa2OwvFrEMTJguCHaoeK1t8URWbuGJSstw-cM");

        Assert.Contains("\"client_id\":\"ara-challenge-overlay\"", stub.SentBody);
        Assert.Contains("\"code_challenge_method\":\"S256\"", stub.SentBody);
        Assert.Contains("E9Melhoa2OwvFrEMTJguCHaoeK1t8URWbuGJSstw-cM", stub.SentBody);
    }

    [Fact]
    public async Task RedeemingSendsTheVerifierAndNotItsChallenge()
    {
        var stub = new Stub(HttpStatusCode.OK, "{}");
        await Client(stub).Redeem("transaction", "verifier");

        Assert.Contains("\"transaction_id\":\"transaction\"", stub.SentBody);
        Assert.Contains("\"code_verifier\":\"verifier\"", stub.SentBody);
    }

    [Fact]
    public async Task APlanIdIsEscapedIntoThePath()
    {
        var stub = new Stub(HttpStatusCode.OK, "{}");
        await Client(stub).GetPlan("token", "../auth/session");

        Assert.DoesNotContain("../", stub.Seen!.RequestUri!.ToString());
    }

    [Fact]
    public async Task ANonJsonBodyComesBackAsTextRatherThanThrowing()
    {
        // Edge rate limits are HTML or plain text; the status has to be readable regardless.
        var stub = new Stub(HttpStatusCode.TooManyRequests, "<html>Too Many Requests</html>", retryAfter: "30");

        var response = await Client(stub).GetPlans("token");

        Assert.Equal(429, response.Status);
        Assert.False(response.Ok);
        Assert.Equal("30", response.RetryAfter);
        Assert.Equal(TimeSpan.FromSeconds(30), RetryPolicy.RetryAfter(response.RetryAfter));
    }

    [Fact]
    public async Task ARedirectIsReportedRatherThanFollowed()
    {
        // Following one would carry the Authorization header to whatever host it named.
        var stub = new Stub(HttpStatusCode.Found, "");

        var response = await Client(stub).GetPlans("token");

        Assert.Equal(302, response.Status);
    }

    [Theory]
    [InlineData(HttpStatusCode.OK, true)]
    [InlineData(HttpStatusCode.Created, true)]
    [InlineData(HttpStatusCode.Accepted, true)]
    [InlineData(HttpStatusCode.NoContent, true)]
    [InlineData(HttpStatusCode.BadRequest, false)]
    [InlineData(HttpStatusCode.Unauthorized, false)]
    [InlineData(HttpStatusCode.ServiceUnavailable, false)]
    public async Task OkFollowsTheStatusCode(HttpStatusCode status, bool expected)
    {
        var response = await Client(new Stub(status, "{}")).GetSession("token");

        Assert.Equal(expected, response.Ok);
    }

    [Fact]
    public async Task RevokingIsADelete()
    {
        var stub = new Stub(HttpStatusCode.NoContent, "");
        var response = await Client(stub).RevokeSession("token");

        Assert.Equal(HttpMethod.Delete, stub.Seen!.Method);
        Assert.Equal("/api/v1/auth/session", stub.Seen.RequestUri!.AbsolutePath);
        Assert.Equal(204, response.Status);
    }

    [Fact]
    public async Task PaginationGoesOnTheQueryString()
    {
        var stub = new Stub(HttpStatusCode.OK, "{}");
        await Client(stub).GetPlans("token", limit: 250, offset: 500);

        Assert.Contains("limit=250", stub.Seen!.RequestUri!.Query);
        Assert.Contains("offset=500", stub.Seen.RequestUri.Query);
    }
}
