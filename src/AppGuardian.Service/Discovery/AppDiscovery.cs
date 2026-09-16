using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using AppGuardian.Shared.Contracts;
using AppGuardian.Shared.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Win32;

namespace AppGuardian.Service.Discovery;

/// <summary>
/// Enumerates installed applications from the registry, Start Menu shortcuts, and packaged apps.
/// FR-400, FR-402, FR-404, FR-405.
/// </summary>
/// <remarks>
/// Three sources are needed because none is complete on its own. Uninstall keys miss portable and
/// per-user tools; Start Menu shortcuts miss anything installed without one; packaged apps appear in
/// neither with a usable executable path. The union is de-duplicated by <c>appId</c> per FR-405, with
/// the source that yields the better display name winning.
/// <para>
/// Results are cached for a short window because the dashboard's picker calls this on open and on
/// every filter keystroke, and a full three-source enumeration costs hundreds of milliseconds — NFR-P1
/// allows two seconds for the whole picker, which a re-scan per keystroke would blow past.
/// </para>
/// </remarks>
[SupportedOSPlatform("windows")]
public sealed class AppDiscovery : IAppDiscovery
{
    private readonly ILogger<AppDiscovery> _log;
    private readonly TimeProvider _clock;
    private readonly SemaphoreSlim _scan = new(1, 1);
    private readonly ConcurrentDictionary<string, string?> _iconCache = new(StringComparer.OrdinalIgnoreCase);

    private List<DiscoveredApp>? _cached;
    private DateTimeOffset _cachedAt;

    public AppDiscovery(ILogger<AppDiscovery> log, TimeProvider? clock = null)
    {
        _log = log;
        _clock = clock ?? TimeProvider.System;
    }

    public async Task<IReadOnlyList<DiscoveredApp>> ListInstalledAsync(CancellationToken ct)
    {
        await _scan.WaitAsync(ct).ConfigureAwait(false);

        try
        {
            if (_cached is not null && _clock.GetUtcNow() - _cachedAt < CacheLifetime)
            {
                return Decorate(_cached);
            }

            // Off the caller's thread: registry and file-system walks are synchronous and would
            // otherwise block the IPC handler for the duration of the scan.
            var scanned = await Task.Run(() => ScanAll(ct), ct).ConfigureAwait(false);

            _cached = scanned;
            _cachedAt = _clock.GetUtcNow();

            return Decorate(scanned);
        }
        finally
        {
            _scan.Release();
        }
    }

    /// <summary>Drops the cache so the next list reflects an install that just happened.</summary>
    public void Invalidate()
    {
        _cached = null;
    }

    public Task<AppIdentity?> ResolveAsync(ResolveIdentityRequest request, CancellationToken ct)
    {
        AppIdentity? identity = null;

        if (request.ProcessId is { } pid and > 0)
        {
            identity = FromProcess(pid);
        }

        if (identity is null && !string.IsNullOrWhiteSpace(request.PackageFamilyName))
        {
            identity = new AppIdentity
            {
                PackageFamilyName = request.PackageFamilyName,
                DisplayName = request.PackageFamilyName!.Split('_')[0],
            };
        }

        if (identity is null && !string.IsNullOrWhiteSpace(request.ExecutablePath))
        {
            identity = FromPath(request.ExecutablePath!);
        }

        if (identity is null)
        {
            return Task.FromResult<AppIdentity?>(null);
        }

        identity.WithDerivedId();

        if (request.ComputeFileHash && identity.ExecutablePath is { } path)
        {
            // Optional because hashing a 200 MB executable is not free, and the hash is only a
            // tamper signal (ADR-004) — never part of identity.
            identity.FileHash = TryHashFile(path);
        }

        return Task.FromResult<AppIdentity?>(identity);
    }

    public async Task<string?> GetIconBase64Async(string appId, CancellationToken ct)
    {
        if (_iconCache.TryGetValue(appId, out var cached))
        {
            return cached;
        }

        var apps = await ListInstalledAsync(ct).ConfigureAwait(false);

        var match = apps.FirstOrDefault(a => a.Identity.AppId == appId);

        // Cached even when null, so a repeated request for an app whose icon cannot be extracted does
        // not re-walk the whole install list each time the picker scrolls past it.
        var icon = match?.Identity.ExecutablePath is { } path ? IconExtractor.TryExtractPng(path, _log) : null;

        _iconCache[appId] = icon;

        return icon;
    }

    private List<DiscoveredApp> ScanAll(CancellationToken ct)
    {
        var byId = new Dictionary<string, DiscoveredApp>(StringComparer.OrdinalIgnoreCase);

        // Order matters: later sources only fill gaps, so the earlier, more authoritative source
        // keeps its display name. Uninstall keys carry the publisher's own product name, which is
        // what the user recognises; a shortcut filename is often abbreviated.
        foreach (var app in ScanUninstallKeys(ct))
        {
            Merge(byId, app);
        }

        foreach (var app in ScanStartMenu(ct))
        {
            Merge(byId, app);
        }

        foreach (var app in ScanPackagedApps(ct))
        {
            Merge(byId, app);
        }

        _log.LogInformation("Discovered {Count} installed applications.", byId.Count);

        return byId.Values
            .OrderBy(a => a.Identity.DisplayName, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
    }

    private static void Merge(Dictionary<string, DiscoveredApp> byId, DiscoveredApp candidate)
    {
        var id = candidate.Identity.AppId;

        if (string.IsNullOrEmpty(id))
        {
            return;
        }

        if (!byId.TryGetValue(id, out var existing))
        {
            byId[id] = candidate;
            return;
        }

        // Fill only what is missing. A shortcut that resolves to an already-known executable adds
        // nothing but may supply an AUMID the registry entry lacked.
        existing.Identity.AppUserModelId ??= candidate.Identity.AppUserModelId;
        existing.Identity.PackageFamilyName ??= candidate.Identity.PackageFamilyName;
        existing.Identity.ExecutablePath ??= candidate.Identity.ExecutablePath;

        if (string.IsNullOrWhiteSpace(existing.Identity.DisplayName))
        {
            existing.Identity.DisplayName = candidate.Identity.DisplayName;
        }
    }

    private IEnumerable<DiscoveredApp> ScanUninstallKeys(CancellationToken ct)
    {
        // Both 64- and 32-bit views plus the per-user hive. Omitting WOW6432Node would silently hide
        // every 32-bit application on a 64-bit machine, which is most older desktop software.
        var roots = new (RegistryHive Hive, RegistryView View)[]
        {
            (RegistryHive.LocalMachine, RegistryView.Registry64),
            (RegistryHive.LocalMachine, RegistryView.Registry32),
            (RegistryHive.CurrentUser, RegistryView.Default),
        };

        foreach (var (hive, view) in roots)
        {
            ct.ThrowIfCancellationRequested();

            foreach (var app in ReadUninstallKey(hive, view))
            {
                yield return app;
            }
        }
    }

    private IEnumerable<DiscoveredApp> ReadUninstallKey(RegistryHive hive, RegistryView view)
    {
        const string Path = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall";

        RegistryKey? uninstall = null;

        try
        {
            using var baseKey = RegistryKey.OpenBaseKey(hive, view);
            uninstall = baseKey.OpenSubKey(Path);
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException)
        {
            _log.LogDebug(ex, "Uninstall key {Hive}/{View} was not readable.", hive, view);
        }

        if (uninstall is null)
        {
            yield break;
        }

        using (uninstall)
        {
            foreach (var name in uninstall.GetSubKeyNames())
            {
                DiscoveredApp? app = null;

                try
                {
                    using var entry = uninstall.OpenSubKey(name);

                    if (entry is null)
                    {
                        continue;
                    }

                    app = FromUninstallEntry(entry);
                }
                catch (Exception ex)
                {
                    _log.LogDebug(ex, "Uninstall entry {Name} could not be read.", name);
                }

                if (app is not null)
                {
                    yield return app;
                }
            }
        }
    }

    private static DiscoveredApp? FromUninstallEntry(RegistryKey entry)
    {
        var display = entry.GetValue("DisplayName") as string;

        if (string.IsNullOrWhiteSpace(display))
        {
            return null;
        }

        // SystemComponent and update entries are real registry entries but not things a user thinks
        // of as apps. Listing them would bury the twenty apps that matter under hundreds that do not.
        if (entry.GetValue("SystemComponent") is int component && component != 0)
        {
            return null;
        }

        if (entry.GetValue("ParentKeyName") is string parent && !string.IsNullOrWhiteSpace(parent))
        {
            return null;
        }

        var exe = ResolveExecutable(entry);

        if (exe is null)
        {
            // No executable means nothing to lock, hide, or throttle. Skipped rather than offered as
            // an entry that would fail on first use.
            return null;
        }

        var identity = new AppIdentity
        {
            DisplayName = display!.Trim(),
            ExecutablePath = exe,
        }.WithDerivedId();

        return new DiscoveredApp
        {
            Identity = identity,
            Source = DiscoverySource.RegistryUninstall,
        };
    }

    private static string? ResolveExecutable(RegistryKey entry)
    {
        // DisplayIcon most often points at the main executable; InstallLocation needs a guess.
        if (entry.GetValue("DisplayIcon") as string is { } icon)
        {
            var path = icon.Split(',')[0].Trim('"', ' ');

            if (path.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) && File.Exists(path))
            {
                return System.IO.Path.GetFullPath(path);
            }
        }

        if (entry.GetValue("InstallLocation") as string is { } location &&
            !string.IsNullOrWhiteSpace(location) &&
            Directory.Exists(location))
        {
            try
            {
                // Top level only. Recursing an install tree would find dozens of helper executables
                // and pick an arbitrary one, which is worse than finding nothing.
                var candidates = Directory.GetFiles(location, "*.exe", SearchOption.TopDirectoryOnly);

                if (candidates.Length == 1)
                {
                    return candidates[0];
                }

                // With several, prefer one whose name resembles the product.
                var display = entry.GetValue("DisplayName") as string ?? string.Empty;

                var best = candidates.FirstOrDefault(c =>
                    display.Contains(
                        System.IO.Path.GetFileNameWithoutExtension(c),
                        StringComparison.OrdinalIgnoreCase));

                return best;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return null;
            }
        }

        return null;
    }

    private IEnumerable<DiscoveredApp> ScanStartMenu(CancellationToken ct)
    {
        var folders = new[]
        {
            Environment.GetFolderPath(Environment.SpecialFolder.CommonStartMenu),
            Environment.GetFolderPath(Environment.SpecialFolder.StartMenu),
        };

        foreach (var folder in folders.Where(f => !string.IsNullOrWhiteSpace(f) && Directory.Exists(f)))
        {
            ct.ThrowIfCancellationRequested();

            string[] shortcuts;

            try
            {
                shortcuts = Directory.GetFiles(folder, "*.lnk", SearchOption.AllDirectories);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _log.LogDebug(ex, "Start Menu folder {Folder} could not be enumerated.", folder);
                continue;
            }

            foreach (var shortcut in shortcuts)
            {
                var target = ShortcutResolver.TryResolveTarget(shortcut, _log);

                if (target is null ||
                    !target.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ||
                    !File.Exists(target))
                {
                    continue;
                }

                yield return new DiscoveredApp
                {
                    Identity = new AppIdentity
                    {
                        DisplayName = System.IO.Path.GetFileNameWithoutExtension(shortcut),
                        ExecutablePath = System.IO.Path.GetFullPath(target),
                    }.WithDerivedId(),
                    Source = DiscoverySource.StartMenuShortcut,
                };
            }
        }
    }

    /// <summary>
    /// Packaged (MSIX/UWP) applications, read from the per-user package repository in the registry.
    /// </summary>
    /// <remarks>
    /// The registry is used rather than the <c>PackageManager</c> WinRT API because this assembly is
    /// plain .NET with no Windows App SDK dependency, and because <c>PackageManager</c> enumeration
    /// from a service session behaves inconsistently across builds.
    /// <para>
    /// Packaged apps have no single controllable executable — they run in a host process — so they are
    /// listed with an <c>UnsupportedReason</c> for the power features rather than silently offered as
    /// though FR-701 would work on them.
    /// </para>
    /// </remarks>
    private IEnumerable<DiscoveredApp> ScanPackagedApps(CancellationToken ct)
    {
        const string Path = @"SOFTWARE\Classes\ActivatableClasses\Package";

        RegistryKey? packages = null;

        try
        {
            packages = Registry.CurrentUser.OpenSubKey(Path);
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException)
        {
            _log.LogDebug(ex, "Packaged app registry key was not readable.");
        }

        if (packages is null)
        {
            yield break;
        }

        var seenFamilies = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        using (packages)
        {
            foreach (var fullName in packages.GetSubKeyNames())
            {
                ct.ThrowIfCancellationRequested();

                // Package full name is Name_Version_Arch__PublisherId; the family is Name_PublisherId.
                var parts = fullName.Split('_');

                if (parts.Length < 5)
                {
                    continue;
                }

                var family = parts[0] + "_" + parts[^1];

                if (!seenFamilies.Add(family))
                {
                    continue;
                }

                yield return new DiscoveredApp
                {
                    Identity = new AppIdentity
                    {
                        DisplayName = parts[0],
                        PackageFamilyName = family,
                    }.WithDerivedId(),
                    Source = DiscoverySource.AppxPackage,
                    UnsupportedReason =
                        "This is a Microsoft Store app. Locking and hiding work, but CPU limits " +
                        "cannot be applied because Store apps share a host process with others.",
                };
            }
        }
    }

    /// <summary>
    /// Stamps live running state onto a cached list. FR-404.
    /// </summary>
    /// <remarks>
    /// Kept out of the cache deliberately: which apps are running changes second to second, so
    /// caching it would show the user stale state, whereas the install list barely changes at all.
    /// </remarks>
    private List<DiscoveredApp> Decorate(List<DiscoveredApp> apps)
    {
        var running = new Dictionary<string, List<int>>(StringComparer.OrdinalIgnoreCase);

        foreach (var process in Process.GetProcesses())
        {
            using (process)
            {
                string? path;

                try
                {
                    path = process.MainModule?.FileName;
                }
                catch (Exception)
                {
                    continue;
                }

                if (string.IsNullOrWhiteSpace(path))
                {
                    continue;
                }

                var id = AppIdentity.DeriveAppId(path, null);

                if (!running.TryGetValue(id, out var pids))
                {
                    pids = new List<int>();
                    running[id] = pids;
                }

                pids.Add(process.Id);
            }
        }

        foreach (var app in apps)
        {
            if (running.TryGetValue(app.Identity.AppId, out var pids))
            {
                app.IsRunning = true;
                app.ProcessIds = pids;
            }
            else
            {
                app.IsRunning = false;
                app.ProcessIds = new List<int>();
            }
        }

        return apps;
    }

    private AppIdentity? FromProcess(int pid)
    {
        try
        {
            using var process = Process.GetProcessById(pid);

            var path = process.MainModule?.FileName;

            if (string.IsNullOrWhiteSpace(path))
            {
                return null;
            }

            return new AppIdentity
            {
                DisplayName = FriendlyName(path) ?? process.ProcessName,
                ExecutablePath = path,
            };
        }
        catch (Exception ex)
        {
            _log.LogDebug(ex, "Pid {Pid} could not be resolved to an identity.", pid);
            return null;
        }
    }

    private static AppIdentity? FromPath(string path)
    {
        if (!File.Exists(path))
        {
            return null;
        }

        var full = System.IO.Path.GetFullPath(path);

        return new AppIdentity
        {
            DisplayName = FriendlyName(full) ?? System.IO.Path.GetFileNameWithoutExtension(full),
            ExecutablePath = full,
        };
    }

    private static string? FriendlyName(string path)
    {
        try
        {
            var info = FileVersionInfo.GetVersionInfo(path);

            // FileDescription is what Task Manager shows and what users recognise; ProductName is
            // often a suite name shared by several executables.
            return string.IsNullOrWhiteSpace(info.FileDescription) ? info.ProductName : info.FileDescription;
        }
        catch (Exception)
        {
            return null;
        }
    }

    private string? TryHashFile(string path)
    {
        try
        {
            using var stream = File.OpenRead(path);
            return "sha256:" + Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _log.LogDebug(ex, "File hash for {Path} could not be computed.", path);
            return null;
        }
    }

    private static readonly TimeSpan CacheLifetime = TimeSpan.FromMinutes(5);
}
