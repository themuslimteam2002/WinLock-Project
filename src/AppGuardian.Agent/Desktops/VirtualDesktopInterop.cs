using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using AppGuardian.Shared.Contracts;
using Microsoft.Extensions.Logging;

namespace AppGuardian.Agent.Desktops;

/// <summary>
/// Virtual desktop COM interop, versioned per Windows build. ADR-002, FR-600.
/// </summary>
/// <remarks>
/// These interfaces are entirely undocumented. Microsoft ships no public API for creating or switching
/// virtual desktops, and the IIDs of <c>IVirtualDesktopManagerInternal</c> change between Windows
/// releases — sometimes between cumulative updates. Nothing here can be relied on.
/// <para>
/// That is why every method is probed at runtime and why the adapter above this has a working fallback
/// (ADR-002). The alternative — shipping no hide feature at all — was rejected because hiding is one of
/// three headline features; the alternative of pretending the API is stable was rejected because it
/// would break silently on a Tuesday.
/// </para>
/// <para>
/// <c>IVirtualDesktopManager</c> (no "Internal") is the one genuinely documented interface, and it can
/// only <em>query</em> which desktop a window is on and move a window to a desktop by GUID. It cannot
/// create a desktop or enumerate them, which is why the internal interfaces are needed at all.
/// </para>
/// </remarks>
[SupportedOSPlatform("windows")]
internal static class VirtualDesktopInterop
{
    /// <summary>CLSID_ImmersiveShell — stable across all supported builds.</summary>
    internal static readonly Guid ImmersiveShellClsid =
        new("C2F03A33-21F5-47FA-B4BB-156362A2F239");

    /// <summary>CLSID_VirtualDesktopManagerInternal — stable; the interface behind it is not.</summary>
    internal static readonly Guid VirtualDesktopManagerInternalClsid =
        new("C5E0CDCA-7B6E-41B2-9FC4-D93975CC467B");

    /// <summary>CLSID_VirtualDesktopManager — the documented, public manager.</summary>
    internal static readonly Guid VirtualDesktopManagerClsid =
        new("AA509086-5CA9-4C25-8F95-589D3C07B48A");

    /// <summary>
    /// Creates a COM object from the immersive shell's service provider.
    /// </summary>
    /// <remarks>
    /// The internal manager is not registered for <c>CoCreateInstance</c>; it is only reachable as a
    /// service of the immersive shell. Calling CoCreateInstance on it returns REGDB_E_CLASSNOTREG,
    /// which is the single most common way this interop is written wrong.
    /// </remarks>
    internal static object? QueryImmersiveShellService(Guid service, Guid iid, ILogger log)
    {
        try
        {
            var shellType = Type.GetTypeFromCLSID(ImmersiveShellClsid);

            if (shellType is null)
            {
                return null;
            }

            var shell = Activator.CreateInstance(shellType);

            if (shell is not IServiceProvider provider)
            {
                return null;
            }

            var hr = provider.QueryService(ref service, ref iid, out var result);

            if (hr != 0 || result == IntPtr.Zero)
            {
                log.LogDebug("QueryService for {Service} returned 0x{Hr:X8}.", service, hr);
                return null;
            }

            try
            {
                return Marshal.GetObjectForIUnknown(result);
            }
            finally
            {
                Marshal.Release(result);
            }
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException or NotSupportedException)
        {
            // Expected on a build whose interface layout differs. The caller falls back.
            log.LogDebug(ex, "Immersive shell service {Service} is unavailable.", service);
            return null;
        }
    }

    [ComImport]
    [Guid("6D5140C1-7436-11CE-8034-00AA006009FA")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IServiceProvider
    {
        [PreserveSig]
        int QueryService(ref Guid service, ref Guid riid, out IntPtr ppvObject);
    }

    /// <summary>The documented manager. Query and move only — cannot create.</summary>
    [ComImport]
    [Guid("A5CD92FF-29BE-454C-8D04-D82879FB3F1B")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IVirtualDesktopManager
    {
        [PreserveSig]
        int IsWindowOnCurrentVirtualDesktop(IntPtr hwnd, [MarshalAs(UnmanagedType.Bool)] out bool onCurrent);

        [PreserveSig]
        int GetWindowDesktopId(IntPtr hwnd, out Guid desktopId);

        [PreserveSig]
        int MoveWindowToDesktop(IntPtr hwnd, ref Guid desktopId);
    }
}
