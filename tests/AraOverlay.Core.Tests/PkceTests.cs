using AraOverlay.Core;

namespace AraOverlay.Core.Tests;

/// <summary>Covers the proof the desktop login is bound to.</summary>
public class PkceTests
{
    [Fact]
    public void VerifierIsTheLengthAndCharsetTheApiAccepts()
    {
        var verifier = Pkce.NewVerifier();

        Assert.Equal(43, verifier.Length);
        Assert.Matches("^[A-Za-z0-9._~-]+$", verifier);
    }

    [Fact]
    public void ChallengeMatchesTheWorkedExampleFromRfc7636()
    {
        // The one published verifier/challenge pair, so a base64url or encoding slip is caught.
        const string verifier = "dBjftJeZ4CVP-mB92K27uhbUJU1p1r_wW1gFWFOEjXk";

        Assert.Equal("E9Melhoa2OwvFrEMTJguCHaoeK1t8URWbuGJSstw-cM", Pkce.Challenge(verifier));
    }
}
