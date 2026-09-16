using System.IO.Pipes;
using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;

namespace AppGuardian.Shared.Ipc;

/// <summary>
/// Builds the named pipe DACL. API Design §2.2, SRS NFR-S2.
/// </summary>
/// <remarks>
/// Access is granted to LocalSystem, Administrators, and the interactive user only; everything else
/// is denied by omission, because a DACL with explicit allow entries and no inherited access grants
/// nothing to anyone unlisted. The ACL is set explicitly at creation and never left to defaults.
/// <para>
/// This is the primary control behind ADR-006: because the pipe boundary already limits callers to
/// those three principals, policy mutations inside that boundary can be gated on an unlock session
/// rather than on token elevation.
/// </para>
/// </remarks>
[SupportedOSPlatform("windows")]
public static class PipeSecurityFactory
{
    /// <summary>
    /// Validates and atomically consumes a caller-bound authorization grant before a mutation.
    /// The validator is supplied by the service to keep the shared IPC assembly independent of the
    /// service's credential and grant storage implementation.
    /// </summary>
    public static bool RequireAuthenticatedSession(
        Func<string?, string?, bool> consumeGrant,
        string? authorizationToken,
        string? clientUserSid) =>
        consumeGrant is not null && consumeGrant(authorizationToken, clientUserSid);

    /// <summary>
    /// DACL for the service pipe. The service runs as LocalSystem and must accept requests from the
    /// dashboard and the agent, both of which run as the interactive user.
    /// </summary>
    public static PipeSecurity ForServicePipe()
    {
        var security = new PipeSecurity();

        Allow(security, WellKnownSidType.LocalSystemSid, PipeAccessRights.FullControl);
        Allow(security, WellKnownSidType.BuiltinAdministratorsSid, PipeAccessRights.FullControl);

        // The interactive user needs to connect, write a request, and read the response.
        // ReadWrite plus CreateNewInstance is deliberately withheld — only the server creates
        // instances, so a rogue user process cannot squat the name after the server exits.
        Allow(security, WellKnownSidType.InteractiveSid, PipeAccessRights.ReadWrite);

        return security;
    }

    /// <summary>
    /// DACL for the agent pipe. The agent runs as the interactive user; the service (LocalSystem)
    /// pushes events to it and the dashboard sends reveal and overlay requests.
    /// </summary>
    public static PipeSecurity ForAgentPipe()
    {
        var security = new PipeSecurity();

        Allow(security, WellKnownSidType.LocalSystemSid, PipeAccessRights.FullControl);
        Allow(security, WellKnownSidType.BuiltinAdministratorsSid, PipeAccessRights.FullControl);
        Allow(security, WellKnownSidType.InteractiveSid, PipeAccessRights.ReadWrite);

        return security;
    }

    private static void Allow(PipeSecurity security, WellKnownSidType sid, PipeAccessRights rights) =>
        security.AddAccessRule(new PipeAccessRule(
            new SecurityIdentifier(sid, domainSid: null),
            rights,
            AccessControlType.Allow));

    /// <summary>
    /// True when the connected client is LocalSystem or a member of Administrators.
    /// </summary>
    /// <remarks>
    /// Used only for the small set of operations that bypass authentication itself — <c>auth.reset</c>
    /// (FR-307) and installer-driven repair. Per ADR-006, ordinary policy mutations are gated on an
    /// active unlock session instead, because the dashboard is a non-elevated process and FR-801
    /// requires it to manage rules.
    /// </remarks>
        public static bool IsElevatedClient(NamedPipeServerStream pipe)
    {
        bool result = false;
        try
        {
            pipe.RunAsClient(() =>
            {
                using var identity = WindowsIdentity.GetCurrent();
                var principal = new WindowsPrincipal(identity);
                result = identity.IsSystem || principal.IsInRole(WindowsBuiltInRole.Administrator);
            });
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Failing closed: an identity we cannot establish is not elevated.
            return false;
        }
        return result;
    }    /// <summary>
    /// Reads the connected client's user SID, used to key per-user credentials (SC-12) and to reject
    /// a caller that somehow got past the DACL.
    /// </summary>
        public static string? GetClientUserSid(NamedPipeServerStream pipe)
    {
        string? sid = null;
        try
        {
            pipe.RunAsClient(() =>
            {
                using var identity = WindowsIdentity.GetCurrent();
                sid = identity.User?.Value;
            });
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
        return sid;
    }
}
