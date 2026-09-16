namespace AppGuardian.Shared.Ipc;

/// <summary>
/// Named pipe identifiers. API Design §2.1.
/// </summary>
/// <remarks>
/// SC-01 RESOLVED 2026-08-25 (ADR-014). The SRS gave two other naming schemes
/// (§8.1 <c>\GuardianUI</c>/<c>\GuardianCtl</c>/<c>\GuardianSvc</c>, §10.1
/// <c>GuardianSvc</c>/<c>GuardianAgent</c>). The owner's ruling is that the service's names — the API
/// Design form used here — are the single source of truth, so the SRS names are dead and this file is the
/// only place either pipe is named. Every process reads these constants; no pipe name is written as a
/// literal anywhere in the solution.
/// <para>
/// The <c>.v1</c> suffix carries the transport version, which API Design §7 requires so old and new pipes
/// can coexist during a migration. Full paths on the wire are therefore
/// <c>\\.\pipe\appguardian.service.v1</c> and <c>\\.\pipe\appguardian.agent.v1</c>; the
/// <c>\\.\pipe\</c> prefix is supplied by Windows and must not be included in these constants.
/// </para>
/// </remarks>
public static class PipeNames
{
    /// <summary>Transport version. Bumped only on a breaking wire-framing change (API Design §7).</summary>
    public const string TransportVersion = "v1";

    /// <summary>Service pipe. Served by AppGuardian.Service as LocalSystem.</summary>
    public const string Service = "appguardian.service." + TransportVersion;

    /// <summary>Agent pipe. Served by AppGuardian.Agent in the interactive session.</summary>
    public const string Agent = "appguardian.agent." + TransportVersion;

    /// <summary>
    /// Maximum accepted message size in bytes. Oversized messages are rejected with
    /// <see cref="ErrorCodes.BadRequest"/> (API Design §2.2).
    /// </summary>
    /// <remarks>
    /// SPEC CONFLICT SC-15c: <c>apps.listInstalled</c> returning <c>iconBase64</c> per app will
    /// exceed 256 KB on a machine with many applications. Our list responses therefore omit icons;
    /// icons are fetched individually via <see cref="MessageTypes.AppsGetIcon"/>.
    /// </remarks>
    public const int MaxMessageBytes = 256 * 1024;
}
