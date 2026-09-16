using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using Microsoft.Extensions.Logging;

namespace AppGuardian.Service.Discovery;

/// <summary>
/// Resolves the target executable of a <c>.lnk</c> shortcut. FR-400.
/// </summary>
/// <remarks>
/// Implemented over <c>IShellLinkW</c> declared inline rather than by taking a dependency on the
/// Windows Script Host COM library (<c>WScript.Shell</c>), which is commonly used for this and is
/// commonly blocked by endpoint security policy — a shortcut scan that fails on managed machines would
/// hide most of the user's apps on exactly the machines where this matters.
/// <para>
/// The alternative of parsing the binary <c>.lnk</c> format directly was rejected: the target lives in
/// a LinkTargetIDList of shell item structures whose layout is undocumented and version-dependent,
/// whereas <c>IShellLinkW</c> is a stable, supported API.
/// </para>
/// </remarks>
[SupportedOSPlatform("windows")]
internal static class ShortcutResolver
{
    public static string? TryResolveTarget(string shortcutPath, ILogger log)
    {
        IShellLinkW? link = null;

        try
        {
            link = (IShellLinkW)new ShellLink();

            var file = (IPersistFile)link;

            file.Load(shortcutPath, StgmRead);

            var buffer = new StringBuilder(MaxPath);

            // SLGP_RAWPATH avoids the environment-variable expansion that SLGP_UNCPRIORITY performs;
            // we expand ourselves so the result is deterministic regardless of the service's own
            // environment, which under LocalSystem differs from the user's.
            link.GetPath(buffer, buffer.Capacity, IntPtr.Zero, SlgpRawPath);

            var raw = buffer.ToString();

            if (string.IsNullOrWhiteSpace(raw))
            {
                // Normal for shortcuts that point at a shell folder, a Store app, or a URL rather than
                // a file. Not an error, and not worth a log line per shortcut.
                return null;
            }

            return Environment.ExpandEnvironmentVariables(raw);
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException or IOException)
        {
            log.LogDebug(ex, "Shortcut {Path} could not be resolved.", shortcutPath);
            return null;
        }
        finally
        {
            if (link is not null)
            {
                // Explicit release: these are RCWs over apartment-threaded COM objects, and leaving
                // thousands of them to the finalizer during a Start Menu walk shows up as a real
                // memory bulge against NFR-P3.
                Marshal.FinalReleaseComObject(link);
            }
        }
    }

    private const int MaxPath = 260;
    private const uint StgmRead = 0x00000000;
    private const uint SlgpRawPath = 0x0004;

    [ComImport]
    [Guid("00021401-0000-0000-C000-000000000046")]
    private class ShellLink
    {
    }

    [ComImport]
    [Guid("000214F9-0000-0000-C000-000000000046")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    [CoClass(typeof(ShellLink))]
    private interface IShellLinkW
    {
        void GetPath(
            [MarshalAs(UnmanagedType.LPWStr)] StringBuilder file,
            int maxPath,
            IntPtr findData,
            uint flags);

        void GetIDList(out IntPtr idList);

        void SetIDList(IntPtr idList);

        void GetDescription([MarshalAs(UnmanagedType.LPWStr)] StringBuilder name, int maxName);

        void SetDescription([MarshalAs(UnmanagedType.LPWStr)] string name);

        void GetWorkingDirectory([MarshalAs(UnmanagedType.LPWStr)] StringBuilder dir, int maxPath);

        void SetWorkingDirectory([MarshalAs(UnmanagedType.LPWStr)] string dir);

        void GetArguments([MarshalAs(UnmanagedType.LPWStr)] StringBuilder args, int maxArgs);

        void SetArguments([MarshalAs(UnmanagedType.LPWStr)] string args);

        void GetHotkey(out short hotkey);

        void SetHotkey(short hotkey);

        void GetShowCmd(out int showCmd);

        void SetShowCmd(int showCmd);

        void GetIconLocation([MarshalAs(UnmanagedType.LPWStr)] StringBuilder iconPath, int maxPath, out int iconIndex);

        void SetIconLocation([MarshalAs(UnmanagedType.LPWStr)] string iconPath, int iconIndex);

        void SetRelativePath([MarshalAs(UnmanagedType.LPWStr)] string path, uint reserved);

        void Resolve(IntPtr hwnd, uint flags);

        void SetPath([MarshalAs(UnmanagedType.LPWStr)] string file);
    }

    [ComImport]
    [Guid("0000010b-0000-0000-C000-000000000046")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IPersistFile
    {
        void GetClassID(out Guid classId);

        [PreserveSig]
        int IsDirty();

        void Load([MarshalAs(UnmanagedType.LPWStr)] string filename, uint mode);

        void Save([MarshalAs(UnmanagedType.LPWStr)] string filename, bool remember);

        void SaveCompleted([MarshalAs(UnmanagedType.LPWStr)] string filename);

        void GetCurFile([MarshalAs(UnmanagedType.LPWStr)] out string filename);
    }
}
