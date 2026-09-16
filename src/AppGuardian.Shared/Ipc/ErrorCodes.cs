namespace AppGuardian.Shared.Ipc;

/// <summary>
/// Canonical error codes. API Design §3.
/// Servers never place stack traces or secrets in <see cref="IpcError.Message"/>;
/// diagnostic detail goes to the local log only.
/// </summary>
public static class ErrorCodes
{
    /// <summary>Malformed envelope or schema violation. Not retryable.</summary>
    public const string BadRequest = "E_BAD_REQUEST";

    /// <summary>The requested <c>apiVersion</c> is not supported by this server. Not retryable.</summary>
    public const string UnsupportedVersion = "E_UNSUPPORTED_VERSION";

    /// <summary>Caller lacks the required privilege or has no active unlock session. Not retryable.</summary>
    public const string Unauthorized = "E_UNAUTHORIZED";

    /// <summary>Windows Hello or PIN verification failed. Retryable until lockout.</summary>
    public const string AuthFailed = "E_AUTH_FAILED";

    /// <summary>Rate limit tripped (FR-306). Retryable after the cooldown expires.</summary>
    public const string AuthLockedOut = "E_AUTH_LOCKED_OUT";

    /// <summary>Rule or app not found. Not retryable.</summary>
    public const string NotFound = "E_NOT_FOUND";

    /// <summary>
    /// Concurrent modification or duplicate rule.
    /// </summary>
    /// <remarks>
    /// SPEC CONFLICT SC-15d: API Design §3 marks this "Maybe" retryable with no rule for deciding.
    /// We treat it as NOT retryable without a fresh read — the client must re-fetch and merge,
    /// because blindly retrying a losing write would silently clobber the winner.
    /// </remarks>
    public const string Conflict = "E_CONFLICT";

    /// <summary>App cannot be locked, hidden, or throttled (FR-509). Not retryable.</summary>
    public const string UnsupportedTarget = "E_UNSUPPORTED_TARGET";

    /// <summary>Virtual Desktop API not usable on this build (SRS Risk 1). Not retryable.</summary>
    public const string VirtualDesktopUnavailable = "E_VD_UNAVAILABLE";

    /// <summary>Service is down; the client fails safe (NFR-S4). Retryable.</summary>
    public const string ServiceUnavailable = "E_SERVICE_UNAVAILABLE";

    /// <summary>Request timed out. Retryable if the operation is idempotent.</summary>
    public const string Timeout = "E_TIMEOUT";

    /// <summary>Unexpected server fault. Retryable at the caller's discretion.</summary>
    public const string Internal = "E_INTERNAL";

    /// <summary>
    /// Returns the default retryability for a code, per API Design §3 with SC-15d resolved.
    /// Callers should still respect the <see cref="IpcError.Retryable"/> flag on the wire,
    /// which allows a server to override per-instance.
    /// </summary>
    public static bool IsRetryableByDefault(string code) => code switch
    {
        AuthFailed => true,
        AuthLockedOut => true,
        ServiceUnavailable => true,
        Timeout => true,
        Internal => true,
        _ => false,
    };
}
