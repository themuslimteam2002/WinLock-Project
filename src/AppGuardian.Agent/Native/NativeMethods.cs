using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace AppGuardian.Agent.Native;

/// <summary>
/// User32/kernel32 interop for the agent. SRS §8.3.
/// </summary>
/// <remarks>
/// Separate from the service's native surface even where a signature is duplicated. The two run at
/// different privilege levels against different APIs, and a shared file would drag the service's
/// job-object surface into an unelevated process that has no business calling it.
/// <para>
/// <c>DllImport</c> rather than <c>LibraryImport</c> throughout. The source generator behind
/// <c>LibraryImport</c> only handles blittable signatures: it cannot marshal the delegate parameters that
/// <c>SetWinEventHook</c> and <c>EnumDisplayMonitors</c> require, nor the fixed-size string inside
/// <c>MONITORINFOEX</c>. Mixing the two styles in one file to save a few marshalling stubs would be a
/// trap for whoever adds the next P/Invoke, so the file is uniform.
/// </para>
/// </remarks>
[SupportedOSPlatform("windows")]
internal static class NativeMethods
{
    // ---- ShowWindow commands ----

    internal const int SwHide = 0;
    internal const int SwShow = 5;
    internal const int SwShowMinNoActive = 7;
    internal const int SwRestore = 9;

    // ---- WinEvent constants ----

    internal const uint EventSystemForeground = 0x0003;
    internal const uint WineventOutOfContext = 0x0000;

    /// <summary>Skip events raised by our own process — the overlay itself takes the foreground.</summary>
    internal const uint WineventSkipOwnProcess = 0x0002;

    internal const uint WmQuit = 0x0012;

    // ---- window queries ----

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool ShowWindow(nint hWnd, int nCmdShow);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool IsWindowVisible(nint hWnd);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool IsIconic(nint hWnd);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool SetForegroundWindow(nint hWnd);

    [DllImport("user32.dll")]
    internal static extern nint GetForegroundWindow();

    [DllImport("user32.dll")]
    internal static extern nint GetShellWindow();

    [DllImport("user32.dll", SetLastError = true)]
    internal static extern uint GetWindowThreadProcessId(nint hWnd, out uint processId);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    internal static extern int GetWindowText(nint hWnd, [Out] char[] text, int count);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    internal static extern int GetWindowTextLength(nint hWnd);

    // ---- window enumeration and classification (FR-601) ----

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, nint lParam);

    internal delegate bool EnumWindowsProc(nint hWnd, nint lParam);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool IsWindow(nint hWnd);

    [DllImport("user32.dll", SetLastError = true)]
    internal static extern nint GetWindow(nint hWnd, uint uCmd);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool GetWindowRect(nint hWnd, out Rect lpRect);

    /// <summary>
    /// Extended window styles.
    /// </summary>
    /// <remarks>
    /// <c>GetWindowLongPtrW</c> rather than <c>GetWindowLongW</c>: on x64 the 32-bit variant truncates
    /// pointer-sized values. Only style bits are read here, so it would happen to work — but the wrong
    /// entry point in an interop file is a defect waiting for the next caller.
    /// </remarks>
    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW", SetLastError = true)]
    internal static extern nint GetWindowLongPtr(nint hWnd, int nIndex);

    internal const uint GwOwner = 4;
    internal const int GwlExstyle = -20;
    internal const long WsExToolwindow = 0x00000080;

    // ---- WinEvent hooks (FR-501) ----

    /// <summary>
    /// Foreground-change notification.
    /// </summary>
    /// <remarks>
    /// A hook, not a poll: NFR-P4 gives one second from an app coming to the foreground to the overlay
    /// covering it, and NFR-P2 caps idle CPU at 2%. Out-of-context so the callback runs on the agent's
    /// own thread rather than being injected into every process on the desktop — in-context hooks would
    /// require a native DLL and would load AppGuardian code into unrelated applications, which is both a
    /// deployment problem and something endpoint security would rightly flag.
    /// </remarks>
    [DllImport("user32.dll", SetLastError = true)]
    internal static extern nint SetWinEventHook(
        uint eventMin,
        uint eventMax,
        nint hmodWinEventProc,
        WinEventProc lpfnWinEventProc,
        uint idProcess,
        uint idThread,
        uint dwFlags);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool UnhookWinEvent(nint hWinEventHook);

    internal delegate void WinEventProc(
        nint hWinEventHook,
        uint eventType,
        nint hwnd,
        int idObject,
        int idChild,
        uint dwEventThread,
        uint dwmsEventTime);

    // ---- message pump for the hook thread ----

    [DllImport("kernel32.dll")]
    internal static extern uint GetCurrentThreadId();

    [DllImport("user32.dll")]
    internal static extern int GetMessage(out Msg lpMsg, nint hWnd, uint filterMin, uint filterMax);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool TranslateMessage(ref Msg lpMsg);

    [DllImport("user32.dll")]
    internal static extern nint DispatchMessage(ref Msg lpMsg);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool PostThreadMessage(uint threadId, uint msg, nint wParam, nint lParam);

    [StructLayout(LayoutKind.Sequential)]
    internal struct Msg
    {
        public nint Hwnd;
        public uint Message;
        public nint WParam;
        public nint LParam;
        public uint Time;
        public int PointX;
        public int PointY;
    }

    // ---- monitors (FR-507: one overlay per monitor) ----

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool EnumDisplayMonitors(
        nint hdc,
        nint lprcClip,
        MonitorEnumProc lpfnEnum,
        nint dwData);

    internal delegate bool MonitorEnumProc(nint hMonitor, nint hdc, ref Rect lprcMonitor, nint dwData);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool GetMonitorInfo(nint hMonitor, ref MonitorInfoEx lpmi);

    /// <summary>Per-monitor DPI. The overlay must cover the whole physical monitor.</summary>
    [DllImport("shcore.dll")]
    internal static extern int GetDpiForMonitor(nint hmonitor, int dpiType, out uint dpiX, out uint dpiY);

    internal const int MdtEffectiveDpi = 0;
    internal const uint MonitorinfofPrimary = 0x00000001;

    [StructLayout(LayoutKind.Sequential)]
    internal struct Rect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;

        public int Width => Right - Left;
        public int Height => Bottom - Top;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    internal struct MonitorInfoEx
    {
        public int Size;
        public Rect Monitor;
        public Rect WorkArea;
        public uint Flags;

        // Inline fixed-size buffer. A plain string field would marshal as a pointer and shift every
        // field after it, which shows up as garbage rectangles rather than as an error.
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        public string DeviceName;
    }

    // ---- process paths (FR-402) ----

    /// <summary>
    /// The full image path of a process.
    /// </summary>
    /// <remarks>
    /// <c>QueryFullProcessImageName</c> rather than <c>Process.MainModule.FileName</c>: the latter needs
    /// PROCESS_VM_READ, which an unelevated agent does not have for most processes, and it throws rather
    /// than failing quietly. This needs only PROCESS_QUERY_LIMITED_INFORMATION.
    /// </remarks>
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool QueryFullProcessImageName(
        nint hProcess,
        uint dwFlags,
        [Out] char[] lpExeName,
        ref uint lpdwSize);

    [DllImport("kernel32.dll", SetLastError = true)]
    internal static extern nint OpenProcess(
        uint access,
        [MarshalAs(UnmanagedType.Bool)] bool inherit,
        uint pid);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool CloseHandle(nint handle);

    internal const uint ProcessQueryLimitedInformation = 0x1000;

    /// <summary>Resolves a PID to its image path, or null when it cannot be read.</summary>
    internal static string? TryGetProcessPath(int pid)
    {
        var handle = OpenProcess(ProcessQueryLimitedInformation, false, (uint)pid);

        if (handle == IntPtr.Zero)
        {
            // Normal, not exceptional: protected processes and processes owned by another user are
            // unreadable from an unelevated agent, and callers treat an unknown path as "not a protected
            // app" rather than as an error.
            return null;
        }

        try
        {
            var buffer = new char[1024];
            var size = (uint)buffer.Length;

            return QueryFullProcessImageName(handle, 0, buffer, ref size)
                ? new string(buffer, 0, (int)size)
                : null;
        }
        finally
        {
            CloseHandle(handle);
        }
    }

    /// <summary>Reads a window's title, or an empty string.</summary>
    internal static string GetWindowTitle(nint hwnd)
    {
        var length = GetWindowTextLength(hwnd);

        if (length <= 0)
        {
            return string.Empty;
        }

        var buffer = new char[length + 1];
        var copied = GetWindowText(hwnd, buffer, buffer.Length);

        return copied > 0 ? new string(buffer, 0, copied) : string.Empty;
    }
}
