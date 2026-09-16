using AppGuardian.Shared.Models;

namespace AppGuardian.Shared.Storage;

/// <summary>
/// Resolves the documented local storage paths. SRS §9.1, NFR-Pr3.
/// </summary>
/// <remarks>
/// Every path is derived here so the installer, service, agent, and uninstaller cannot disagree
/// about where data lives — a disagreement would surface as FR-103's "remove user data" leaving
/// files behind.
/// </remarks>
public static class StoragePaths
{
    /// <summary>
    /// Machine-wide data root: <c>%ProgramData%\AppGuardian</c>.
    /// ACL per SRS §9.1: Administrators and LocalSystem write, Users read.
    /// </summary>
    public static string MachineRoot => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
        PolicyConstants.DataFolderName);

    /// <summary>Per-user data root: <c>%LOCALAPPDATA%\AppGuardian</c>.</summary>
    public static string UserRoot => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        PolicyConstants.DataFolderName);

    public static string PolicyFile => Path.Combine(MachineRoot, PolicyConstants.PolicyFileName);

    public static string CredentialsFile => Path.Combine(MachineRoot, PolicyConstants.CredentialsFileName);

    public static string AuditFile => Path.Combine(MachineRoot, PolicyConstants.AuditFileName);

    public static string SettingsFile => Path.Combine(UserRoot, PolicyConstants.SettingsFileName);

    /// <summary>Service and agent diagnostic logs. Distinct from the audit log, which is a record, not a log.</summary>
    public static string LogFolder => Path.Combine(MachineRoot, "logs");

    /// <summary>Creates the data folders if absent. Callers needing ACLs apply them separately.</summary>
    public static void EnsureFolders()
    {
        Directory.CreateDirectory(MachineRoot);
        Directory.CreateDirectory(LogFolder);
        Directory.CreateDirectory(UserRoot);
    }
}
