using System.Security.Cryptography;
using System.Text;

namespace AraOverlay.Core;

/// <summary>
/// The proof that binds a login redemption to the app that started it. The verifier never leaves
/// the machine; only its SHA-256 goes to the server, so an intercepted transaction id is useless.
/// </summary>
public static class Pkce
{
    /// <summary>Makes a fresh verifier.</summary>
    /// <returns>43 base64url characters, from 32 cryptographically random bytes.</returns>
    public static string NewVerifier() => Encode(RandomNumberGenerator.GetBytes(32));

    /// <summary>Derives the challenge the server stores against the transaction.</summary>
    /// <param name="verifier">The verifier held in memory.</param>
    /// <returns>Unpadded base64url of SHA-256 over the verifier's ASCII bytes.</returns>
    public static string Challenge(string verifier) => Encode(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));

    /// <summary>base64url, which .NET 8 has no built-in for.</summary>
    /// <param name="bytes">What to encode.</param>
    /// <returns>Base64 with the URL alphabet and no padding.</returns>
    private static string Encode(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
