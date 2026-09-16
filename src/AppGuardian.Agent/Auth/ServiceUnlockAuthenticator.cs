using System.Runtime.Versioning;
using AppGuardian.Shared.Contracts;
using AppGuardian.Shared.Ipc;
using AppGuardian.Shared.Models;
using Microsoft.Extensions.Logging;

// AuthMethod is defined in AppGuardian.Shared.Models.AuthStatus (auth.verify pin/session), which
// is why the Models import above is required on this file — without it the StartSessionAsync
// signature below fails CS0246 against IUnlockAuthenticator.

namespace AppGuardian.Agent.Auth;

/// <summary>
/// Verifies unlocks — Hello locally, secrets through the service. FR-300–FR-306.
/// </summary>
/// <remarks>
/// The split is a security boundary, not a convenience. Hello has to run here because the sensor and the
/// consent UI only exist in the interactive session. Secrets have to be verified in the service because
/// that is where the salted hash and the FR-306 lockout counter live, and an unelevated process holding
/// either could brute-force them offline at its leisure.
/// </remarks>
[SupportedOSPlatform("windows10.0.17763.0")]
public sealed class ServiceUnlockAuthenticator : IUnlockAuthenticator
{
    private readonly IWindowsHelloVerifier _hello;
    private readonly ServiceConnection _service;
    private readonly ILogger<ServiceUnlockAuthenticator> _log;

    public ServiceUnlockAuthenticator(
        IWindowsHelloVerifier hello,
        ServiceConnection service,
        ILogger<ServiceUnlockAuthenticator> log)
    {
        _hello = hello;
        _service = service;
        _log = log;
    }

    public Task<bool> IsHelloAvailableAsync() => _hello.IsAvailableAsync();

    public async Task<VerifyResult> VerifyHelloAsync(string reason, CancellationToken ct)
    {
        // The service initiates the actual prompt through its AgentBridge and only then issues the
        // single-use grant. This prevents any local pipe caller from asserting Hello success.
        var response = await _service
            .SendAsync(
                MessageTypes.AuthVerifyWindowsHello,
                new AuthVerifyRequest(),
                ct: ct)
            .ConfigureAwait(false);

        if (response?.Success != true)
        {
            return VerifyResult.Unavailable(
                response?.Error?.Message ?? "Windows Hello could not be used right now. Use your PIN instead.");
        }

        var result = response.PayloadAs<AuthVerifyResult>();
        return result?.Verified == true
            ? VerifyResult.Verified()
            : VerifyResult.Failed(result?.Reason);
    }

    public async Task<PinAttemptOutcome> VerifySecretAsync(string secret, string appId, CancellationToken ct)
    {
        var response = await _service
            .SendAsync(
                MessageTypes.AuthVerifyPin,
                new AuthVerifyRequest { Secret = secret, AppId = appId },
                ct: ct)
            .ConfigureAwait(false);

        if (response is null)
        {
            // Distinguished from a wrong PIN on purpose. Telling the user "incorrect PIN" when the
            // service is down would have them retrying a correct secret until the lockout they never
            // earned, and the overlay shows a different message for this case (FR-905).
            return PinAttemptOutcome.Unavailable(
                "AppGuardian's background service is not responding, so your PIN cannot be checked right now.");
        }

        if (response.Success == true)
        {
            var result = response.PayloadAs<AuthVerifyResult>();

            return result?.Verified == true
                ? PinAttemptOutcome.Success()
                : new PinAttemptOutcome { Verified = false, Message = "That PIN is not correct." };
        }

        var error = response.Error;
        var lockedOut = error?.Code == ErrorCodes.AuthLockedOut;

        return new PinAttemptOutcome
        {
            Verified = false,
            LockedOut = lockedOut,

            // Read from the error details rather than re-derived here: the service owns the counter, and
            // a second opinion computed in the agent would drift the moment either side changed.
            AttemptsRemaining = ReadInt(error, "attemptsRemaining"),
            LockedUntilUtc = ReadTime(error, "lockedUntilUtc"),

            // The service's message is already user-facing and already avoids saying anything about why
            // the secret was wrong, so it is passed through rather than replaced.
            Message = string.IsNullOrWhiteSpace(error?.Message)
                ? "That PIN is not correct."
                : error!.Message,

            ServiceUnavailable = error?.Code == ErrorCodes.ServiceUnavailable,
        };
    }

    public async Task<bool> StartSessionAsync(string appId, AuthMethod method, CancellationToken ct)
    {
        var response = await _service
            .SendAsync(
                MessageTypes.AuthStartSession,
                new StartSessionRequest { AppId = appId },
                ct: ct)
            .ConfigureAwait(false);

        if (response?.Success == true)
        {
            return true;
        }

        // A failure here is worth logging loudly: the user has authenticated successfully and the overlay
        // is about to come down, but the service does not know about it, so the next foreground switch
        // will lock the app again. That looks like a bug to the user, and this line is what explains it.
        _log.LogError(
            "The unlock session for {AppId} could not be registered: {Error}",
            appId,
            response?.Error?.Code ?? "no response");

        return false;
    }

    private static int? ReadInt(IpcError? error, string key)
    {
        if (error?.Details is null || !error.Details.TryGetValue(key, out var raw) || raw is null)
        {
            return null;
        }

        // The value arrives as a JsonElement across the wire and as a boxed int in-process (tests).
        // Convert.ToInt32 handles both without a type switch per numeric kind.
        try
        {
            return raw is System.Text.Json.JsonElement element
                ? element.ValueKind == System.Text.Json.JsonValueKind.Number
                    ? element.GetInt32()
                    : null
                : Convert.ToInt32(raw);
        }
        catch (Exception ex) when (ex is FormatException or InvalidCastException or OverflowException)
        {
            return null;
        }
    }

    private static DateTimeOffset? ReadTime(IpcError? error, string key)
    {
        if (error?.Details is null || !error.Details.TryGetValue(key, out var raw) || raw is null)
        {
            return null;
        }

        var text = raw is System.Text.Json.JsonElement element
            ? element.ValueKind == System.Text.Json.JsonValueKind.String ? element.GetString() : null
            : raw.ToString();

        return DateTimeOffset.TryParse(text, out var parsed) ? parsed : null;
    }
}
