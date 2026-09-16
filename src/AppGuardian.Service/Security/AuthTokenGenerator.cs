using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Security.Principal;

namespace AppGuardian.Service.Security;

/// <summary>
/// Issues short-lived, single-use authorization grants after a successful interactive authentication.
/// </summary>
public sealed class AuthTokenGenerator
{
    private static readonly TimeSpan Lifetime = TimeSpan.FromSeconds(30);

    private readonly ConcurrentDictionary<string, Grant> _grants = new(StringComparer.Ordinal);
    private readonly TimeProvider _clock;

    public AuthTokenGenerator(TimeProvider? clock = null) => _clock = clock ?? TimeProvider.System;

    /// <summary>Issues a grant for the process identity. Intended for interactive callers only.</summary>
    public string Generate() => GenerateForSid(
        WindowsIdentity.GetCurrent().User?.Value
        ?? throw new InvalidOperationException("The current Windows identity has no user SID."));

    /// <summary>Issues a grant for the SID established by the named-pipe impersonation boundary.</summary>
    public string GenerateForSid(string userSid)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(userSid);

        var bytes = RandomNumberGenerator.GetBytes(32);
        var token = Convert.ToBase64String(bytes);
        var grant = new Grant(bytes, userSid, _clock.GetUtcNow() + Lifetime);

        // A 256-bit collision is not realistically recoverable, but never overwrite a valid grant.
        while (!_grants.TryAdd(token, grant))
        {
            bytes = RandomNumberGenerator.GetBytes(32);
            token = Convert.ToBase64String(bytes);
            grant = new Grant(bytes, userSid, _clock.GetUtcNow() + Lifetime);
        }

        return token;
    }

    /// <summary>
    /// Atomically consumes a grant only when it belongs to the caller and has not expired.
    /// </summary>
    public bool TryConsume(string? token, string? userSid)
    {
        if (string.IsNullOrWhiteSpace(token) || string.IsNullOrWhiteSpace(userSid))
        {
            return false;
        }

        byte[] supplied;
        try
        {
            supplied = Convert.FromBase64String(token);
        }
        catch (FormatException)
        {
            return false;
        }

        if (supplied.Length != 32 || !_grants.TryRemove(token, out var grant))
        {
            return false;
        }

        // Do not short-circuit: compare the complete random token and SID after atomic removal.
        var tokenMatches = CryptographicOperations.FixedTimeEquals(supplied, grant.TokenBytes);
        var sidMatches = CryptographicOperations.FixedTimeEquals(
            System.Text.Encoding.UTF8.GetBytes(userSid),
            System.Text.Encoding.UTF8.GetBytes(grant.UserSid));

        return tokenMatches && sidMatches && grant.ExpiresUtc >= _clock.GetUtcNow();
    }

    private sealed record Grant(byte[] TokenBytes, string UserSid, DateTimeOffset ExpiresUtc);
}
