using System.Text.Json;

namespace AraOverlay.Core;

/// <summary>Where a login has got to.</summary>
public enum LoginState
{
    Idle,
    Starting,
    WaitingForBrowser,
    Succeeded,
    Failed,
}

/// <summary>What the panel should be showing about a login.</summary>
/// <param name="State">Where it has got to.</param>
/// <param name="UserCode">The code the member types into the browser.</param>
/// <param name="Expires">When the transaction stops being redeemable.</param>
/// <param name="Message">A line for the member, or empty.</param>
public sealed record LoginStatus(LoginState State, string UserCode, DateTimeOffset Expires, string Message)
{
    public static readonly LoginStatus Idle = new(LoginState.Idle, "", default, "");
}

/// <summary>The token a completed login produced.</summary>
/// <param name="AccessToken">The opaque ARA bearer.</param>
/// <param name="Expires">When it stops working.</param>
public readonly record struct LoginResult(string AccessToken, DateTimeOffset Expires);

/// <summary>
/// Runs one desktop login from start to finish: opens a transaction, hands the member a code,
/// then polls until it is redeemed, expires, or fails for good.
///
/// The verifier lives in a local for the life of the call and is never written anywhere. A
/// successful redemption is never retried — the transaction is consumed exactly once, so a lost
/// 200 means starting again, not asking twice.
/// </summary>
public sealed class LoginController(
    ApiClient api,
    Action<string> openBrowser,
    Func<TimeSpan, CancellationToken, Task>? delay = null)
{
    private readonly Func<TimeSpan, CancellationToken, Task> _delay = delay ?? ((wait, token) => Task.Delay(wait, token));

    public event Action<LoginStatus>? Changed;

    /// <summary>Runs a login to completion.</summary>
    /// <param name="cancel">Cancels the login; the member closing it, or the app exiting.</param>
    /// <returns>The token on success, or null with the reason already reported through Changed.</returns>
    public async Task<LoginResult?> Run(CancellationToken cancel = default)
    {
        try
        {
            return await Poll(cancel).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // The member closed the login, or the overlay is exiting. Nothing to report.
            Report(LoginStatus.Idle);
            return null;
        }
    }

    /// <summary>Runs the transaction, letting cancellation escape to Run.</summary>
    /// <param name="cancel">Cancels the login.</param>
    /// <returns>The token on success, or null.</returns>
    private async Task<LoginResult?> Poll(CancellationToken cancel)
    {
        var verifier = Pkce.NewVerifier();

        Report(new LoginStatus(LoginState.Starting, "", default, "Contacting the Academy…"));

        var started = await Attempt(() => api.StartLogin(Pkce.Challenge(verifier), cancel), cancel).ConfigureAwait(false);

        if (started is not { Status: 201 })
        {
            Fail(started, "Could not start the login.");
            return null;
        }

        if (Transaction(started.Value.Body) is not { } transaction)
        {
            Fail(null, "The Academy's reply could not be read.");
            return null;
        }

        openBrowser(transaction.VerificationUri);

        var deadline = DateTimeOffset.UtcNow.AddSeconds(transaction.ExpiresIn);
        var wait = TimeSpan.FromSeconds(Math.Max(1, transaction.Interval));

        Report(new LoginStatus(LoginState.WaitingForBrowser, transaction.UserCode, deadline,
            "Waiting for you to finish in your browser…"));

        while (!cancel.IsCancellationRequested && DateTimeOffset.UtcNow < deadline)
        {
            await _delay(wait, cancel).ConfigureAwait(false);

            var polled = await Attempt(() => api.Redeem(transaction.Id, verifier, cancel), cancel).ConfigureAwait(false);

            if (polled is not { } response)
            {
                Fail(null, "The Academy could not be reached.");
                return null;
            }

            switch (response.Status)
            {
                case 200 when Session(response.Body) is { } session:
                    Report(new LoginStatus(LoginState.Succeeded, "", deadline, ""));
                    return session;

                case 200:
                    // Redeemed, but the token was unreadable. Replaying is refused, so this login
                    // is spent whatever happens next.
                    Fail(null, "Signed in, but the reply could not be read. Please sign in again.");
                    return null;

                case 202:
                    continue;

                case 429:
                    wait = RetryPolicy.RetryAfter(response.RetryAfter);
                    continue;

                case var status when RetryPolicy.IsTransient(status):
                    wait = RetryPolicy.Backoff(1);
                    continue;

                default:
                    Fail(response, "Login was refused.");
                    return null;
            }
        }

        Report(new LoginStatus(LoginState.Failed, "", deadline,
            cancel.IsCancellationRequested ? "" : "The code expired. Sign in again from the tray."));

        return null;
    }

    /// <summary>Sends one request, retrying a temporary failure a few times before giving up.</summary>
    /// <param name="send">The call to make.</param>
    /// <param name="cancel">Cancels the login.</param>
    /// <returns>The response, or null when it never got through.</returns>
    private async Task<ApiResponse?> Attempt(Func<Task<ApiResponse>> send, CancellationToken cancel)
    {
        for (var attempt = 1; ; attempt++)
        {
            ApiResponse response;

            try
            {
                response = await send().ConfigureAwait(false);
            }
            catch (Exception e) when (e is HttpRequestException or TaskCanceledException && !cancel.IsCancellationRequested)
            {
                if (attempt > 3) return null;
                await _delay(RetryPolicy.Backoff(attempt), cancel).ConfigureAwait(false);
                continue;
            }

            if (attempt > 3 || !RetryPolicy.IsTransient(response.Status)) return response;

            await _delay(RetryPolicy.Backoff(attempt), cancel).ConfigureAwait(false);
        }
    }

    /// <summary>Reports a failure, preferring the server's own wording where the docs give one.</summary>
    /// <param name="response">What came back, if anything.</param>
    /// <param name="fallback">What to say otherwise.</param>
    private void Fail(ApiResponse? response, string fallback) =>
        Report(new LoginStatus(LoginState.Failed, "", default, response switch
        {
            { Status: 403 } => "An eligible Gold membership is required.",
            { Status: 410 } => "The code expired. Sign in again from the tray.",
            { Status: 503 } => "The Academy's API is unavailable. Try again shortly.",
            _ => fallback,
        }));

    /// <summary>Raises Changed, which callers marshal to their own thread.</summary>
    /// <param name="status">What to report.</param>
    private void Report(LoginStatus status) => Changed?.Invoke(status);

    /// <summary>Reads a created transaction.</summary>
    /// <param name="body">The 201 body.</param>
    /// <returns>The transaction, or null if a field is missing.</returns>
    private static (string Id, string UserCode, string VerificationUri, int ExpiresIn, int Interval)? Transaction(string body)
    {
        var root = Root(body);

        return ApiCatalog.Text(root, "transaction_id") is { } id &&
               ApiCatalog.Text(root, "user_code") is { } userCode &&
               ApiCatalog.Text(root, "verification_uri") is { } uri &&
               Uri.TryCreate(uri, UriKind.Absolute, out var parsed) &&
               parsed.Scheme == Uri.UriSchemeHttps
            ? (id, userCode, uri, Number(root, "expires_in", 600), Number(root, "interval", 5))
            : null;
    }

    /// <summary>Reads a redeemed session.</summary>
    /// <param name="body">The 200 body.</param>
    /// <returns>The token and its expiry, or null if the token is missing.</returns>
    private static LoginResult? Session(string body)
    {
        var root = Root(body);

        // expires_in is read rather than assumed, so a server-side change needs no new build.
        return ApiCatalog.Text(root, "access_token") is { Length: > 0 } token
            ? new LoginResult(token, DateTimeOffset.UtcNow.AddSeconds(Number(root, "expires_in", 43200)))
            : null;
    }

    /// <summary>Parses a body that may not be JSON at all.</summary>
    /// <param name="body">The response body.</param>
    /// <returns>The root element, or an undefined one.</returns>
    private static JsonElement Root(string body)
    {
        try
        {
            return JsonDocument.Parse(body).RootElement.Clone();
        }
        catch (JsonException)
        {
            return default;
        }
    }

    /// <summary>Reads an integer property.</summary>
    private static int Number(JsonElement element, string name, int fallback) =>
        element.ValueKind == JsonValueKind.Object &&
        element.TryGetProperty(name, out var value) &&
        value.ValueKind == JsonValueKind.Number &&
        value.TryGetInt32(out var number)
            ? number
            : fallback;
}
