using System.Runtime.Versioning;
using AppGuardian.Shared.Contracts;
using Microsoft.Extensions.Logging;
using Windows.Security.Credentials.UI;

namespace AppGuardian.Agent.Auth;

/// <summary>
/// Windows Hello verification via <c>UserConsentVerifier</c>. FR-300, FR-301.
/// </summary>
/// <remarks>
/// <c>UserConsentVerifier</c> rather than <c>KeyCredentialManager</c>. The latter would give a real
/// asymmetric key attested by the TPM, which is cryptographically stronger, but it requires a
/// registered credential per app and — more importantly — an Azure AD or Microsoft account to enrol
/// against. AppGuardian must work on a local account with a PIN, and the SRS says so (FR-302's fallback
/// exists precisely for machines with no Hello at all).
/// <para>
/// The honest consequence: this is a consent prompt, not an authentication token. Anything running as
/// the interactive user could call the same API and get the same "yes". That is an accepted residual
/// risk (SRS §11) — AppGuardian protects against someone at the keyboard, not against code the user is
/// already running.
/// </para>
/// </remarks>
[SupportedOSPlatform("windows10.0.17763.0")]
public sealed class WindowsHelloVerifier : IWindowsHelloVerifier
{
    private readonly ILogger<WindowsHelloVerifier> _log;

    public WindowsHelloVerifier(ILogger<WindowsHelloVerifier> log) => _log = log;

    public async Task<bool> IsAvailableAsync()
    {
        try
        {
            var availability = await UserConsentVerifier
                .CheckAvailabilityAsync()
                .AsTask()
                .ConfigureAwait(false);

            // DeviceBusy is treated as available on purpose: the sensor is there, it is just occupied
            // right now, and reporting Hello as absent would push the user permanently onto the PIN
            // fallback because of a transient state.
            return availability is UserConsentVerifierAvailability.Available
                or UserConsentVerifierAvailability.DeviceBusy;
        }
        catch (Exception ex)
        {
            // The WinRT projection throws rather than returning a value on systems where the contract
            // is missing entirely — Server SKUs, stripped images. Absent, not broken.
            _log.LogDebug(ex, "Windows Hello availability could not be determined.");
            return false;
        }
    }

    public async Task<VerifyResult> VerifyAsync(string reason, CancellationToken ct)
    {
        try
        {
            var result = await UserConsentVerifier
                .RequestVerificationAsync(reason)
                .AsTask(ct)
                .ConfigureAwait(false);

            return result switch
            {
                UserConsentVerificationResult.Verified => VerifyResult.Verified(),

                // Cancellation is not a failure and must not count against the lockout budget
                // (FR-306): a user who dismisses the prompt to go and find their PIN has done nothing
                // wrong, and five dismissals should not lock them out for fifteen minutes.
                UserConsentVerificationResult.Canceled => VerifyResult.Cancelled(),

                UserConsentVerificationResult.DeviceNotPresent =>
                    VerifyResult.Unavailable("This device has no Windows Hello sensor set up."),

                UserConsentVerificationResult.NotConfiguredForUser =>
                    VerifyResult.Unavailable("Windows Hello is not set up for your account."),

                UserConsentVerificationResult.DisabledByPolicy =>
                    VerifyResult.Unavailable("Windows Hello has been turned off by a system policy."),

                UserConsentVerificationResult.DeviceBusy =>
                    VerifyResult.Unavailable("The Windows Hello sensor is busy. Try again in a moment."),

                UserConsentVerificationResult.RetriesExhausted =>
                    VerifyResult.Failed("Windows Hello did not recognise you. Use your PIN instead."),

                _ => VerifyResult.Failed(),
            };
        }
        catch (OperationCanceledException)
        {
            // Propagating this would make a cancelled unlock look like a crash to the caller, which
            // then has to distinguish "user pressed Escape" from "something went wrong" by exception
            // type. Cancelled is a first-class outcome instead.
            return VerifyResult.Cancelled();
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Windows Hello verification failed.");

            return VerifyResult.Unavailable(
                "Windows Hello could not be used right now. Use your PIN instead.");
        }
    }
}
