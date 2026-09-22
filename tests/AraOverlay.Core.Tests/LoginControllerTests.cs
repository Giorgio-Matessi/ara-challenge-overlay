using System.Net;
using AraOverlay.Core;

namespace AraOverlay.Core.Tests;

/// <summary>
/// Covers one desktop login end to end. The rules that matter are the ones a real server only
/// enforces once: never poll faster than told, never redeem twice, never treat pending as failure.
/// </summary>
public class LoginControllerTests
{
    /// <summary>Answers each request from a queue, recording what it was sent.</summary>
    private sealed class Script(params (HttpStatusCode Status, string Body, string? RetryAfter)[] replies) : HttpMessageHandler
    {
        private int _next;

        public List<string> Bodies { get; } = [];
        public List<string> Paths { get; } = [];

        public int Calls => _next;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancel)
        {
            Paths.Add(request.RequestUri!.AbsolutePath);
            Bodies.Add(request.Content is null ? "" : await request.Content.ReadAsStringAsync(cancel));

            var (status, body, retryAfter) = replies[Math.Min(_next++, replies.Length - 1)];

            var response = new HttpResponseMessage(status) { Content = new StringContent(body) };
            if (retryAfter is not null) response.Headers.TryAddWithoutValidation("Retry-After", retryAfter);

            return response;
        }
    }

    private const string Started = """
        {"transaction_id":"tx","user_code":"12345678",
         "verification_uri":"https://labs.almeidaracingacademy.com/activate",
         "expires_in":600,"interval":5}
        """;

    private const string Redeemed = """
        {"access_token":"ara_token","token_type":"Bearer","expires_in":43200,"scope":"training-plans:read"}
        """;

    private const string Pending = """{"status":"authorization_pending","interval":5}""";

    /// <summary>Builds a controller whose waits are instant and whose browser is a list.</summary>
    private static (LoginController Login, List<string> Opened, List<TimeSpan> Waits, List<LoginStatus> Reported)
        Build(Script script)
    {
        var opened = new List<string>();
        var waits = new List<TimeSpan>();
        var reported = new List<LoginStatus>();

        var login = new LoginController(
            new ApiClient(new Uri("https://labs.example"), script),
            opened.Add,
            (wait, _) => { waits.Add(wait); return Task.CompletedTask; });

        login.Changed += reported.Add;

        return (login, opened, waits, reported);
    }

    [Fact]
    public async Task ACompletedLoginReturnsTheToken()
    {
        var script = new Script(
            (HttpStatusCode.Created, Started, null),
            (HttpStatusCode.Accepted, Pending, null),
            (HttpStatusCode.OK, Redeemed, null));

        var (login, opened, _, reported) = Build(script);
        var result = await login.Run();

        Assert.Equal("ara_token", result!.Value.AccessToken);
        Assert.Equal(["https://labs.almeidaracingacademy.com/activate"], opened);
        Assert.Equal(LoginState.Succeeded, reported[^1].State);
    }

    [Fact]
    public async Task TheCodeIsReportedForTheMemberToType()
    {
        var script = new Script(
            (HttpStatusCode.Created, Started, null),
            (HttpStatusCode.OK, Redeemed, null));

        var (login, _, _, reported) = Build(script);
        await login.Run();

        var waiting = reported.Single(s => s.State == LoginState.WaitingForBrowser);
        Assert.Equal("12345678", waiting.UserCode);
    }

    [Fact]
    public async Task TheChallengeSentIsTheHashOfTheVerifierSent()
    {
        // The whole point of the proof: a transaction id on its own must be useless.
        var script = new Script(
            (HttpStatusCode.Created, Started, null),
            (HttpStatusCode.OK, Redeemed, null));

        var (login, _, _, _) = Build(script);
        await login.Run();

        var challenge = Between(script.Bodies[0], "\"code_challenge\":\"", "\"");
        var verifier = Between(script.Bodies[1], "\"code_verifier\":\"", "\"");

        Assert.Equal(challenge, Pkce.Challenge(verifier));
        Assert.DoesNotContain(verifier, script.Bodies[0]);
    }

    [Fact]
    public async Task ASuccessfulRedemptionIsNeverRepeated()
    {
        // The transaction is consumed exactly once; polling again cannot recover anything.
        var script = new Script(
            (HttpStatusCode.Created, Started, null),
            (HttpStatusCode.OK, Redeemed, null));

        var (login, _, _, _) = Build(script);
        await login.Run();

        Assert.Equal(2, script.Calls);
    }

    [Fact]
    public async Task PendingIsNotAFailure()
    {
        var script = new Script(
            (HttpStatusCode.Created, Started, null),
            (HttpStatusCode.Accepted, Pending, null),
            (HttpStatusCode.Accepted, Pending, null),
            (HttpStatusCode.Accepted, Pending, null),
            (HttpStatusCode.OK, Redeemed, null));

        var (login, _, _, reported) = Build(script);

        Assert.NotNull(await login.Run());
        Assert.DoesNotContain(reported, s => s.State == LoginState.Failed);
    }

    [Fact]
    public async Task PollingWaitsTheIntervalTheServerAsksFor()
    {
        var script = new Script(
            (HttpStatusCode.Created, Started, null),
            (HttpStatusCode.OK, Redeemed, null));

        var (login, _, waits, _) = Build(script);
        await login.Run();

        Assert.Equal(TimeSpan.FromSeconds(5), waits[0]);
    }

    [Fact]
    public async Task ARateLimitSwitchesToTheServersRetryAfter()
    {
        var script = new Script(
            (HttpStatusCode.Created, Started, null),
            (HttpStatusCode.TooManyRequests, "<html>slow down</html>", "30"),
            (HttpStatusCode.OK, Redeemed, null));

        var (login, _, waits, _) = Build(script);

        Assert.NotNull(await login.Run());
        Assert.Equal(TimeSpan.FromSeconds(30), waits[^1]);
    }

    [Theory]
    [InlineData(HttpStatusCode.Gone, "expired")]
    [InlineData(HttpStatusCode.Forbidden, "Gold")]
    public async Task ATerminalRefusalStopsAndSaysWhy(HttpStatusCode status, string expected)
    {
        var script = new Script(
            (HttpStatusCode.Created, Started, null),
            (status, """{"error":"denied"}""", null));

        var (login, _, _, reported) = Build(script);

        Assert.Null(await login.Run());
        Assert.Equal(LoginState.Failed, reported[^1].State);
        Assert.Contains(expected, reported[^1].Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(2, script.Calls);
    }

    [Fact]
    public async Task AFailureToStartNeverOpensABrowser()
    {
        var script = new Script((HttpStatusCode.ServiceUnavailable, """{"error":"paused"}""", null));

        var (login, opened, _, reported) = Build(script);

        Assert.Null(await login.Run());
        Assert.Empty(opened);
        Assert.Equal(LoginState.Failed, reported[^1].State);
    }

    [Fact]
    public async Task AVerificationUriThatIsNotHttpsIsRefused()
    {
        // The member is about to type a code into whatever this opens.
        var script = new Script((HttpStatusCode.Created,
            Started.Replace("https://labs.almeidaracingacademy.com/activate", "http://labs.example/activate"), null));

        var (login, opened, _, reported) = Build(script);

        Assert.Null(await login.Run());
        Assert.Empty(opened);
        Assert.Equal(LoginState.Failed, reported[^1].State);
    }

    [Fact]
    public async Task ATransientFailureIsRetriedThenGivesUp()
    {
        var script = new Script((HttpStatusCode.BadGateway, """{"error":"upstream"}""", null));

        var (login, _, _, reported) = Build(script);

        Assert.Null(await login.Run());
        Assert.Equal(LoginState.Failed, reported[^1].State);
        Assert.InRange(script.Calls, 2, 5);
    }

    [Fact]
    public async Task CancellingStopsQuietlyWithNothingToSay()
    {
        // The member closed the login. No error belongs on the panel.
        var script = new Script(
            (HttpStatusCode.Created, Started, null),
            (HttpStatusCode.Accepted, Pending, null));

        var reported = new List<LoginStatus>();
        using var cancel = new CancellationTokenSource();

        var login = new LoginController(
            new ApiClient(new Uri("https://labs.example"), script),
            _ => { },
            (_, token) =>
            {
                cancel.Cancel();
                return Task.FromCanceled(token.IsCancellationRequested ? token : cancel.Token);
            });

        login.Changed += reported.Add;

        Assert.Null(await login.Run(cancel.Token));
        Assert.Equal(LoginState.Idle, reported[^1].State);
    }

    /// <summary>Pulls a value out of a request body without parsing it.</summary>
    private static string Between(string text, string start, string end)
    {
        var from = text.IndexOf(start, StringComparison.Ordinal) + start.Length;
        return text[from..text.IndexOf(end, from, StringComparison.Ordinal)];
    }
}
