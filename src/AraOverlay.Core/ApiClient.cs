using System.Net.Http.Headers;
using System.Text;

namespace AraOverlay.Core;

/// <summary>One ARA response, before anything tries to read it as JSON.</summary>
/// <param name="Status">The HTTP status code.</param>
/// <param name="Body">The body as text; edge failures are not always JSON.</param>
/// <param name="RetryAfter">The Retry-After header, when there was one.</param>
public readonly record struct ApiResponse(int Status, string Body, string? RetryAfter)
{
    public bool Ok => Status is >= 200 and < 300;
}

/// <summary>
/// Talks to the ARA Labs API and nothing else. Every call hands back the status and the raw body:
/// the contract is explicit that an edge error may not be JSON and that the status has to be read
/// before the body, so parsing is the caller's decision.
///
/// Redirects are refused outright. A redirect the client followed would carry the Authorization
/// header to whatever host it pointed at, which is the one way a bearer token leaves this machine
/// by accident.
/// </summary>
public sealed class ApiClient : IDisposable
{
    private const string ClientId = "ara-challenge-overlay";

    private static readonly TimeSpan PostTimeout = TimeSpan.FromSeconds(27);
    private static readonly TimeSpan GetTimeout = TimeSpan.FromSeconds(24);

    private readonly HttpClient _http;

    /// <summary>Builds a client against the live API.</summary>
    /// <param name="baseAddress">Where Labs lives; defaults to the production host.</param>
    /// <param name="handler">A handler to use instead of the default, for tests.</param>
    public ApiClient(Uri? baseAddress = null, HttpMessageHandler? handler = null)
    {
        // Forced rather than only set on the default, so a handler passed in can't quietly lose it.
        if (handler is HttpClientHandler passed) passed.AllowAutoRedirect = false;

        _http = new HttpClient(handler ?? new HttpClientHandler { AllowAutoRedirect = false })
        {
            BaseAddress = baseAddress ?? new Uri("https://labs.almeidaracingacademy.com"),
            Timeout = System.Threading.Timeout.InfiniteTimeSpan,
        };
    }

    public void Dispose() => _http.Dispose();

    /// <summary>Starts a desktop login.</summary>
    /// <param name="codeChallenge">The S256 challenge; the verifier stays with the caller.</param>
    /// <param name="cancel">Cancels the request.</param>
    /// <returns>A 201 carrying the transaction, user code and verification URI.</returns>
    public Task<ApiResponse> StartLogin(string codeChallenge, CancellationToken cancel = default) =>
        Post("/api/v1/auth/transactions",
            $$"""
            {"client_id":"{{ClientId}}","code_challenge_method":"S256","code_challenge":"{{codeChallenge}}"}
            """,
            cancel);

    /// <summary>Polls for a completed login, redeeming it once the member has signed in.</summary>
    /// <param name="transactionId">The transaction being redeemed.</param>
    /// <param name="codeVerifier">The verifier the challenge was derived from.</param>
    /// <param name="cancel">Cancels the request.</param>
    /// <returns>200 with a session, 202 while still pending, or a terminal status.</returns>
    public Task<ApiResponse> Redeem(string transactionId, string codeVerifier, CancellationToken cancel = default) =>
        Post("/api/v1/auth/token",
            $$"""
            {"transaction_id":"{{transactionId}}","code_verifier":"{{codeVerifier}}"}
            """,
            cancel);

    /// <summary>Lists the training plans the member can see.</summary>
    /// <param name="token">The ARA bearer token.</param>
    /// <param name="limit">Page size, 1 to 1000.</param>
    /// <param name="offset">Where the page starts.</param>
    /// <param name="cancel">Cancels the request.</param>
    /// <returns>The collection response.</returns>
    public Task<ApiResponse> GetPlans(string token, int limit = 100, int offset = 0, CancellationToken cancel = default) =>
        Get($"/api/v1/garage61/training-plans?limit={limit}&offset={offset}", token, cancel);

    /// <summary>Reads one plan's challenges, targets and simulator mappings.</summary>
    /// <param name="token">The ARA bearer token.</param>
    /// <param name="planId">The plan's ULID.</param>
    /// <param name="cancel">Cancels the request.</param>
    /// <returns>The detail response.</returns>
    public Task<ApiResponse> GetPlan(string token, string planId, CancellationToken cancel = default) =>
        Get($"/api/v1/garage61/training-plans/{Uri.EscapeDataString(planId)}", token, cancel);

    /// <summary>Checks the session is still live and still Gold.</summary>
    /// <param name="token">The ARA bearer token.</param>
    /// <param name="cancel">Cancels the request.</param>
    /// <returns>The session response.</returns>
    public Task<ApiResponse> GetSession(string token, CancellationToken cancel = default) =>
        Get("/api/v1/auth/session", token, cancel);

    /// <summary>Revokes this app's session. The member stays signed in to the Academy in their browser.</summary>
    /// <param name="token">The ARA bearer token.</param>
    /// <param name="cancel">Cancels the request.</param>
    /// <returns>204 when revoked or already gone.</returns>
    public Task<ApiResponse> RevokeSession(string token, CancellationToken cancel = default) =>
        Send(new HttpRequestMessage(HttpMethod.Delete, "/api/v1/auth/session"), token, PostTimeout, cancel);

    /// <summary>Sends a JSON POST with no credentials, as both login routes take.</summary>
    /// <param name="path">The route.</param>
    /// <param name="json">The request body.</param>
    /// <param name="cancel">Cancels the request.</param>
    /// <returns>The response.</returns>
    private Task<ApiResponse> Post(string path, string json, CancellationToken cancel) =>
        Send(new HttpRequestMessage(HttpMethod.Post, path)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json"),
        }, token: null, PostTimeout, cancel);

    /// <summary>Sends an authenticated GET.</summary>
    /// <param name="path">The route.</param>
    /// <param name="token">The ARA bearer token.</param>
    /// <param name="cancel">Cancels the request.</param>
    /// <returns>The response.</returns>
    private Task<ApiResponse> Get(string path, string token, CancellationToken cancel) =>
        Send(new HttpRequestMessage(HttpMethod.Get, path), token, GetTimeout, cancel);

    /// <summary>
    /// Sends one request under its own time budget, and reads the response as text whatever it
    /// turned out to be.
    /// </summary>
    /// <param name="request">The request to send.</param>
    /// <param name="token">The bearer token, or null for the unauthenticated login routes.</param>
    /// <param name="timeout">How long to allow, a little under the server's own budget.</param>
    /// <param name="cancel">Cancels the request.</param>
    /// <returns>The status, body and Retry-After.</returns>
    private async Task<ApiResponse> Send(HttpRequestMessage request, string? token, TimeSpan timeout, CancellationToken cancel)
    {
        using (request)
        {
            if (token is not null)
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancel);
            deadline.CancelAfter(timeout);

            using var response = await _http.SendAsync(request, deadline.Token).ConfigureAwait(false);

            return new ApiResponse(
                (int)response.StatusCode,
                await response.Content.ReadAsStringAsync(deadline.Token).ConfigureAwait(false),
                response.Headers.TryGetValues("Retry-After", out var values) ? values.FirstOrDefault() : null);
        }
    }
}
