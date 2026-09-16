using System.Runtime.Versioning;
using AppGuardian.Shared.Contracts;
using AppGuardian.Shared.Models;

namespace AppGuardian.Agent.Auth;

/// <summary>
/// What the overlay needs in order to accept an unlock. FR-502, FR-505.
/// </summary>
/// <remarks>
/// The overlay draws and collects input; it does not decide. Windows Hello is verified locally in this
/// process (the sensor only exists in the interactive session), but a PIN or password is verified by the
/// service, because the agent must never see the stored hash, the salt, or the lockout counter — an
/// unelevated process that could read those could also brute-force them offline (SRS §11, ADR-006).
/// <para>
/// Abstracted rather than calling the pipe client directly so the overlay can be exercised in a test
/// without a running service.
/// </para>
/// </remarks>
[SupportedOSPlatform("windows")]
public interface IUnlockAuthenticator
{
    /// <summary>Whether to offer the Hello button at all. FR-302.</summary>
    Task<bool> IsHelloAvailableAsync();

    /// <summary>Prompts for Hello. The reason string is shown by Windows, so it must be user-facing.</summary>
    Task<VerifyResult> VerifyHelloAsync(string reason, CancellationToken ct);

    /// <summary>
    /// Sends the entered secret to the service for verification.
    /// </summary>
    /// <remarks>
    /// Returns the service's own result — including <c>attemptsRemaining</c> and the lockout window — so
    /// the overlay can show the user how much room they have left rather than counting attempts itself.
    /// A local counter would reset every time the agent restarted, which is trivially bypassable.
    /// </remarks>
    Task<PinAttemptOutcome> VerifySecretAsync(string secret, string appId, CancellationToken ct);

    /// <summary>
    /// Asks the service to grant an unlock session after a successful Hello verification.
    /// </summary>
    /// <remarks>
    /// A separate call from verification because the PIN path already establishes the session as part of
    /// verifying: only the Hello path needs to assert its result to the service afterwards.
    /// </remarks>
    Task<bool> StartSessionAsync(string appId, AuthMethod method, CancellationToken ct);
}

/// <summary>Outcome of a secret attempt, flattened for the overlay's benefit.</summary>
public sealed class PinAttemptOutcome
{
    public bool Verified { get; init; }

    /// <summary>Null when the service did not say — treated as "unknown", not as zero.</summary>
    public int? AttemptsRemaining { get; init; }

    public bool LockedOut { get; init; }

    public DateTimeOffset? LockedUntilUtc { get; init; }

    /// <summary>User-facing message. Never contains a reason a secret was wrong.</summary>
    public string? Message { get; init; }

    /// <summary>True when the service could not be reached at all, which is not an auth failure.</summary>
    public bool ServiceUnavailable { get; init; }

    public static PinAttemptOutcome Success() => new() { Verified = true };

    public static PinAttemptOutcome Unavailable(string message) =>
        new() { Verified = false, ServiceUnavailable = true, Message = message };
}
