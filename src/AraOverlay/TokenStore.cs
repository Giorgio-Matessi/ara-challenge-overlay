using System.IO;
using System.Security.Cryptography;
using System.Text;
using AraOverlay.Core;

namespace AraOverlay;

/// <summary>An ARA session as it is held on disk.</summary>
/// <param name="Token">The opaque bearer token.</param>
/// <param name="Expires">When the server said it stops working.</param>
public readonly record struct StoredSession(string Token, DateTimeOffset Expires)
{
    /// <summary>Whether the token is still worth sending, with a minute's margin.</summary>
    public bool Usable => Token.Length > 0 && Expires - DateTimeOffset.UtcNow > TimeSpan.FromMinutes(1);
}

/// <summary>
/// Keeps the ARA bearer token in %APPDATA%, encrypted with DPAPI under the current Windows
/// account, so copying the file to another machine or another user yields nothing. The token
/// never goes anywhere else: not a log, not a setting, not the window title.
/// </summary>
public static class TokenStore
{
    private static string FilePath => JsonFile.PathIn("session.dat");

    /// <summary>Writes the session, replacing any previous one.</summary>
    /// <param name="session">The token and its expiry.</param>
    public static void Save(StoredSession session)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            File.WriteAllBytes(FilePath, ProtectedData.Protect(
                Encoding.UTF8.GetBytes($"{session.Expires:O}\n{session.Token}"),
                optionalEntropy: null,
                DataProtectionScope.CurrentUser));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or CryptographicException)
        {
        }
    }

    /// <summary>Reads the stored session.</summary>
    /// <returns>The session, or null when there isn't one, or it belongs to someone else.</returns>
    public static StoredSession? Load()
    {
        try
        {
            if (!File.Exists(FilePath)) return null;

            var plain = Encoding.UTF8.GetString(ProtectedData.Unprotect(
                File.ReadAllBytes(FilePath), optionalEntropy: null, DataProtectionScope.CurrentUser));

            var split = plain.Split('\n', 2);

            return split.Length == 2 && DateTimeOffset.TryParse(split[0], out var expires)
                ? new StoredSession(split[1], expires)
                : null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or CryptographicException)
        {
            return null;
        }
    }

    /// <summary>Removes the stored session, on sign-out or after a 401.</summary>
    public static void Clear()
    {
        try
        {
            File.Delete(FilePath);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
        }
    }
}
