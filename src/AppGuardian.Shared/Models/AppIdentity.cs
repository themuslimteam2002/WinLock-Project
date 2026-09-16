using System.Security.Cryptography;
using System.Text;

namespace AppGuardian.Shared.Models;

/// <summary>
/// How an application is uniquely referenced. API Design §5, SRS FR-402.
/// </summary>
/// <remarks>
/// SPEC CONFLICT SC-05: SRS §9.2 names these fields <c>exePath</c>, <c>aumid</c>, <c>sha256</c>
/// and has no <c>appId</c> at all. We use the API Design names per ADR-009, because <c>appId</c> is
/// the key every other message in the catalog uses to reference an app.
/// </remarks>
public sealed class AppIdentity
{
    /// <summary>
    /// Stable derived key, format <c>sha256:&lt;hex&gt;</c>. See <see cref="DeriveAppId"/>.
    /// </summary>
    public string AppId { get; set; } = string.Empty;

    public string DisplayName { get; set; } = string.Empty;

    /// <summary>Fully resolved path. Null for packaged apps with no meaningful single executable.</summary>
    public string? ExecutablePath { get; set; }

    /// <summary>Set for UWP/MSIX apps. Stable across updates and install locations.</summary>
    public string? PackageFamilyName { get; set; }

    public string? AppUserModelId { get; set; }

    /// <summary>
    /// Optional SHA-256 of the executable's contents, format <c>sha256:&lt;hex&gt;</c>.
    /// </summary>
    /// <remarks>
    /// A tamper/update <em>signal</em> only. Deliberately excluded from identity derivation — see
    /// ADR-004. If this participated in <see cref="AppId"/>, every application update would orphan
    /// the user's rules.
    /// </remarks>
    public string? FileHash { get; set; }

    /// <summary>
    /// Base64 PNG icon. Omitted from list responses to stay under the 256 KB envelope cap
    /// (SC-15c); fetch individually via <c>apps.getIcon</c>.
    /// </summary>
    public string? IconBase64 { get; set; }

    /// <summary>
    /// Derives the stable <see cref="AppId"/> from identity, never from file content (ADR-004).
    /// </summary>
    /// <remarks>
    /// SPEC CONFLICT SC-06: API Design §5 calls <c>appId</c> a "stable derived key" but never says
    /// what is hashed. We hash a normalised identity string: the lowercased
    /// <paramref name="packageFamilyName"/> when present (genuinely stable across updates and
    /// install locations), otherwise the lowercased executable path. Consequence: a rule survives
    /// an app update but not a move or reinstall to a different path. This is the whole of the
    /// derivation — a different ruling changes only this method.
    /// </remarks>
    public static string DeriveAppId(string? executablePath, string? packageFamilyName)
    {
        string normalised;

        if (!string.IsNullOrWhiteSpace(packageFamilyName))
        {
            normalised = "pfn:" + packageFamilyName.Trim().ToLowerInvariant();
        }
        else if (!string.IsNullOrWhiteSpace(executablePath))
        {
            // Normalise separators and case so C:/Foo\App.EXE and c:\foo\app.exe agree.
            normalised = "exe:" + executablePath.Trim()
                .Replace('/', '\\')
                .ToLowerInvariant();
        }
        else
        {
            throw new ArgumentException(
                "An app identity requires either an executable path or a package family name.");
        }

        var digest = SHA256.HashData(Encoding.UTF8.GetBytes(normalised));
        return "sha256:" + Convert.ToHexString(digest).ToLowerInvariant();
    }

    /// <summary>Populates <see cref="AppId"/> from the current path/PFN. Returns this for chaining.</summary>
    public AppIdentity WithDerivedId()
    {
        AppId = DeriveAppId(ExecutablePath, PackageFamilyName);
        return this;
    }

    /// <summary>True when this identity refers to a packaged (UWP/MSIX) application.</summary>
    public bool IsPackaged => !string.IsNullOrWhiteSpace(PackageFamilyName);
}
