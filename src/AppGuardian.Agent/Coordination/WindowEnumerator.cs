using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using AppGuardian.Agent.Native;

namespace AppGuardian.Agent.Coordination;

/// <summary>
/// Finds the top-level windows belonging to a process. FR-601.
/// </summary>
/// <remarks>
/// A process can own many windows and most of them should not be touched. The filter keeps only windows
/// that a user could plausibly see and interact with, because hiding an app's invisible message-only
/// windows achieves nothing and hiding its tooltips or IME candidate windows can wedge its input.
/// </remarks>
[SupportedOSPlatform("windows")]
internal static class WindowEnumerator
{
    /// <summary>Visible, non-owned top-level windows for a PID, in z-order.</summary>
    internal static IEnumerable<nint> TopLevelWindowsFor(int processId)
    {
        var found = new List<nint>();

        NativeMethods.EnumWindows(
            (hwnd, _) =>
            {
                if (IsHideable(hwnd, processId))
                {
                    found.Add(hwnd);
                }

                return true;
            },
            IntPtr.Zero);

        return found;
    }

    private static bool IsHideable(nint hwnd, int processId)
    {
        if (!NativeMethods.IsWindowVisible(hwnd))
        {
            return false;
        }

        NativeMethods.GetWindowThreadProcessId(hwnd, out var pid);

        if ((int)pid != processId)
        {
            return false;
        }

        // An owned window — a dialog, a tooltip, a dropdown — follows its owner. Hiding it independently
        // leaves the app in a state it never expects, and hiding the owner takes it along anyway.
        if (NativeMethods.GetWindow(hwnd, NativeMethods.GwOwner) != IntPtr.Zero)
        {
            return false;
        }

        var exStyle = (long)NativeMethods.GetWindowLongPtr(hwnd, NativeMethods.GwlExstyle);

        // Tool windows are palettes and floating panels. They are excluded for the same reason they are
        // excluded from Alt+Tab: they are not the window the user thinks of as "the app".
        if ((exStyle & NativeMethods.WsExToolwindow) != 0)
        {
            return false;
        }

        // A zero-area window is either a message sink or a not-yet-laid-out shell. Neither is visible to
        // the user, and both are common enough that including them would inflate every hide count.
        return NativeMethods.GetWindowRect(hwnd, out var rect) && rect.Width > 0 && rect.Height > 0;
    }

    /// <summary>
    /// Whether a handle still belongs to the process it was recorded against.
    /// </summary>
    /// <remarks>
    /// Window handles are reused. A reveal that trusted a stale handle would restore an unrelated
    /// window — quite possibly another user's application — into the foreground, so every recorded handle
    /// is re-checked against its original PID before being acted on.
    /// </remarks>
    internal static bool BelongsToProcess(nint hwnd, int processId)
    {
        if (hwnd == IntPtr.Zero || !NativeMethods.IsWindow(hwnd))
        {
            return false;
        }

        NativeMethods.GetWindowThreadProcessId(hwnd, out var pid);

        return (int)pid == processId;
    }

    /// <summary>The best guess at an app's main window, for overlay placement.</summary>
    internal static nint MainWindowFor(int processId)
    {
        // First in z-order wins. EnumWindows returns top-most first, so the frontmost visible window of
        // the process is the one the user is looking at — a better answer than Process.MainWindowHandle,
        // which caches the first window the process ever created.
        foreach (var hwnd in TopLevelWindowsFor(processId))
        {
            return hwnd;
        }

        return IntPtr.Zero;
    }
}
