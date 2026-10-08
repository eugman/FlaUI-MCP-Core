using System.Runtime.InteropServices;
using System.Text;

namespace FlaUI.Mcp.Core;

/// <summary>
/// Lightweight information about a top-level Win32 window, gathered without
/// any UI Automation calls.
/// </summary>
/// <param name="Hwnd">Native window handle.</param>
/// <param name="Title">Window title (may be empty).</param>
/// <param name="ProcessId">Owning process id.</param>
/// <param name="IsEnabled">Whether the window accepts input (modal dialogs disable their owner).</param>
/// <param name="IsToolWindow">Whether the window has WS_EX_TOOLWINDOW (excluded from window lists).</param>
/// <param name="IsCloaked">Whether the window is DWM-cloaked (e.g., suspended UWP apps).</param>
public sealed record Win32WindowInfo(
    nint Hwnd,
    string Title,
    int ProcessId,
    bool IsEnabled,
    bool IsToolWindow,
    bool IsCloaked
);

/// <summary>
/// Win32-based desktop window enumeration and manipulation.
/// All APIs used here read cached window metadata and never block on the target
/// process's message loop, so they remain usable while a UI Automation provider
/// is blocked (e.g., by a modal dialog opened from a pending Invoke call).
/// </summary>
public static partial class Win32Desktop
{
    private const int GWL_EXSTYLE = -20;
    private const long WS_EX_TOOLWINDOW = 0x00000080;
    private const int DWMWA_CLOAKED = 14;
    private const int SW_RESTORE = 9;
    private const uint WM_CLOSE = 0x0010;

    private delegate bool EnumWindowsProc(nint hWnd, nint lParam);

    private const uint CF_UNICODETEXT = 13;
    private const uint GMEM_MOVEABLE = 0x0002;
    private const nint HWND_MESSAGE = -3;

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool OpenClipboard(nint hWndNewOwner);

    [DllImport("user32.dll")]
    private static extern bool CloseClipboard();

    [DllImport("user32.dll")]
    private static extern bool EmptyClipboard();

    [DllImport("user32.dll", SetLastError = true)]
    private static extern nint SetClipboardData(uint format, nint handle);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern uint RegisterClipboardFormat(string name);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern nint CreateWindowEx(
        int exStyle,
        string className,
        string? windowName,
        int style,
        int x,
        int y,
        int width,
        int height,
        nint parent,
        nint menu,
        nint instance,
        nint param
    );

    [DllImport("user32.dll")]
    private static extern bool DestroyWindow(nint hWnd);

    [DllImport("kernel32.dll")]
    private static extern nint GlobalAlloc(uint flags, nuint bytes);

    [DllImport("kernel32.dll")]
    private static extern nint GlobalLock(nint memory);

    [DllImport("kernel32.dll")]
    private static extern bool GlobalUnlock(nint memory);

    [DllImport("kernel32.dll")]
    private static extern nint GlobalFree(nint memory);

    [DllImport("user32.dll")]
    private static extern nint GetClipboardData(uint format);

    [DllImport("user32.dll")]
    private static extern bool IsClipboardFormatAvailable(uint format);

    [DllImport("user32.dll")]
    private static extern nint GetClipboardOwner();

    private const long WS_EX_TRANSPARENT = 0x00000020;
    private const long WS_EX_LAYERED = 0x00080000;
    private const int LWA_ALPHA = 0x00000002;

    [DllImport("user32.dll")]
    private static extern bool GetLayeredWindowAttributes(nint hWnd, out uint crKey, out byte bAlpha, out uint dwFlags);

    /// <summary>
    /// What a clipboard read produced. A bare string could not tell "the clipboard is empty" from
    /// "another process held it open", and silently dropped everything past the cap.
    /// </summary>
    /// <param name="Text">The text read, or null when there was none or it could not be read.</param>
    /// <param name="Truncated">Whether text beyond the cap was dropped.</param>
    /// <param name="Failure">Why the read failed, or null when it succeeded.</param>
    public readonly record struct ClipboardText(string? Text, bool Truncated, string? Failure);

    /// <summary>
    /// The process that last put data on the clipboard, or 0 when it is unknown (the owner exited,
    /// or the data was set without an owning window). This is the clipboard's origin; the focused
    /// window is not — focus can be moved to an allowed app by this server's own tools.
    /// </summary>
    public static int GetClipboardOwnerProcessId()
    {
        var owner = GetClipboardOwner();
        if (owner == 0)
        {
            return 0;
        }

        GetWindowThreadProcessId(owner, out var pid);
        return (int)pid;
    }

    /// <summary>
    /// Read the clipboard's Unicode text, or null when it holds none or cannot be opened.
    /// Needed because some editors expose no text to UI Automation at all — a Scintilla-based one
    /// has no Value or Text pattern, so selecting and copying is the only way to read it.
    /// </summary>
    /// <param name="maxCharacters">Truncate beyond this, so a large copied buffer cannot flood a caller.</param>
    /// <returns>The text with a truncation flag, or a stated failure.</returns>
    public static ClipboardText GetClipboardText(int maxCharacters = 20000)
    {
        if (!IsClipboardFormatAvailable(CF_UNICODETEXT))
        {
            return new(null, false, null);
        }

        var opened = false;
        for (var attempt = 0; attempt < 10 && !(opened = OpenClipboard(0)); attempt++)
        {
            Thread.Sleep(50);
        }

        if (!opened)
        {
            return new(null, false, "The clipboard is held open by another process and could not be read.");
        }

        try
        {
            var handle = GetClipboardData(CF_UNICODETEXT);
            if (handle == 0)
            {
                return new(null, false, "The clipboard reported text but returned no data for it.");
            }

            var pointer = GlobalLock(handle);
            if (pointer == 0)
            {
                return new(null, false, "The clipboard's text could not be locked for reading.");
            }

            try
            {
                var text = Marshal.PtrToStringUni(pointer) ?? string.Empty;
                return new(Truncate(text, maxCharacters), text.Length > maxCharacters, null);
            }
            finally
            {
                GlobalUnlock(handle);
            }
        }
        finally
        {
            CloseClipboard();
        }
    }

    /// <summary>Cut to a length without splitting a surrogate pair, which would yield a lone half.</summary>
    internal static string Truncate(string text, int maxCharacters)
    {
        if (text.Length <= maxCharacters)
        {
            return text;
        }

        var end = maxCharacters;
        if (end > 0 && char.IsHighSurrogate(text[end - 1]))
        {
            end--;
        }

        return text[..end];
    }

    /// <summary>
    /// Replace the clipboard with Unicode text, excluded from clipboard history.
    /// A message-only window owns it, since an ownerless EmptyClipboard makes SetClipboardData fail.
    /// Returns false when another process keeps the clipboard open.
    /// </summary>
    public static bool SetClipboardText(string text)
    {
        var owner = CreateWindowEx(0, "STATIC", null, 0, 0, 0, 0, 0, HWND_MESSAGE, 0, 0, 0);
        try
        {
            var opened = false;
            for (var attempt = 0; attempt < 10 && !(opened = OpenClipboard(owner)); attempt++)
            {
                Thread.Sleep(50);
            }

            if (!opened)
            {
                return false;
            }

            try
            {
                EmptyClipboard();
                SetClipboardBytes(CF_UNICODETEXT, Encoding.Unicode.GetBytes(text + "\0"));
                // Keeping the text out of clipboard history is best effort; the paste still works without it.
                try
                {
                    SetClipboardBytes(
                        RegisterClipboardFormat("ExcludeClipboardContentFromMonitorProcessing"),
                        new byte[4]
                    );
                }
                catch (InvalidOperationException) { }

                return true;
            }
            finally
            {
                CloseClipboard();
            }
        }
        finally
        {
            if (owner != 0)
            {
                DestroyWindow(owner);
            }
        }
    }

    private static void SetClipboardBytes(uint format, byte[] bytes)
    {
        var memory = GlobalAlloc(GMEM_MOVEABLE, (nuint)bytes.Length);
        if (memory == 0)
        {
            throw new InvalidOperationException("Clipboard memory allocation failed.");
        }

        var pointer = GlobalLock(memory);
        if (pointer == 0)
        {
            GlobalFree(memory);
            throw new InvalidOperationException("Clipboard memory could not be locked.");
        }

        try
        {
            Marshal.Copy(bytes, 0, pointer, bytes.Length);
        }
        finally
        {
            GlobalUnlock(memory);
        }
        // On success the clipboard owns the memory.
        if (SetClipboardData(format, memory) == 0)
        {
            GlobalFree(memory);
            throw new InvalidOperationException("Writing to the clipboard failed.");
        }
    }

    [DllImport("user32.dll")]
    private static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, nint lParam);

    [DllImport("user32.dll")]
    public static extern bool IsWindowVisible(nint hWnd);

    [DllImport("user32.dll")]
    public static extern bool IsWindowEnabled(nint hWnd);

    [DllImport("user32.dll")]
    private static extern bool IsIconic(nint hWnd);

    /// <summary>True while the window is minimized; UIA trees of minimized windows are often incomplete.</summary>
    public static bool IsMinimized(nint hwnd) => IsIconic(hwnd);

    [DllImport("user32.dll")]
    private static extern nint GetLastActivePopup(nint hWnd);

    /// <summary>The window Windows activates for <paramref name="owner"/>: its last active owned popup, or itself.</summary>
    public static nint LastActivePopup(nint owner) => GetLastActivePopup(owner);

    [DllImport("user32.dll")]
    private static extern bool IsZoomed(nint hWnd);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetWindowPos(
        nint hwnd,
        nint insertAfter,
        int x,
        int y,
        int width,
        int height,
        uint flags
    );

    public static bool IsNormalWindow(nint hwnd) => IsWindowVisible(hwnd) && !IsIconic(hwnd) && !IsZoomed(hwnd);

    public static void PlaceWindow(nint hwnd, System.Drawing.Rectangle bounds)
    {
        // SWP_NOZORDER | SWP_NOACTIVATE | SWP_NOOWNERZORDER. Synchronous, one attempt.
        if (!SetWindowPos(hwnd, 0, bounds.X, bounds.Y, bounds.Width, bounds.Height, 0x0004 | 0x0010 | 0x0200))
        {
            throw new System.ComponentModel.Win32Exception(
                Marshal.GetLastWin32Error(),
                "Window placement failed; mutation not replayed"
            );
        }
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowText(nint hWnd, StringBuilder lpString, int nMaxCount);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(nint hWnd, out uint lpdwProcessId);

    [DllImport("user32.dll")]
    private static extern nint GetWindowLongPtr(nint hWnd, int nIndex);

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(nint hWnd);

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(nint hWnd, int nCmdShow);

    [DllImport("user32.dll")]
    private static extern bool PostMessage(nint hWnd, uint msg, nint wParam, nint lParam);

    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(nint hWnd, out RECT lpRect);

    [DllImport("user32.dll")]
    public static extern nint GetForegroundWindow();

    [DllImport("user32.dll")]
    public static extern nint GetAncestor(nint hwnd, uint flags);

    [DllImport("user32.dll")]
    private static extern nint GetWindow(nint hwnd, uint command);

    /// <summary>True when <paramref name="owner"/> occurs in a window's Win32 owner chain.</summary>
    public static bool HasOwner(nint hwnd, nint owner)
    {
        // GW_OWNER. Cap the walk so corrupt/native-reused window relationships
        // cannot turn an ownership check into an unbounded traversal.
        for (var depth = 0; hwnd != 0 && depth < 16; depth++)
        {
            hwnd = GetWindow(hwnd, 4);
            if (hwnd == owner)
            {
                return true;
            }
        }

        return false;
    }

    [DllImport("user32.dll")]
    private static extern nint WindowFromPoint(System.Drawing.Point point);

    public static int GetProcessId(nint hwnd)
    {
        GetWindowThreadProcessId(hwnd, out var pid);
        return (int)pid;
    }

    public static nint WindowAt(System.Drawing.Point point) => GetAncestor(WindowFromPoint(point), 2);

    /// <summary>Raw window under a screen point, before any ancestor walk.</summary>
    public static nint WindowFromScreenPoint(System.Drawing.Point point) => WindowFromPoint(point);

    /// <summary>
    /// True when <paramref name="hwnd"/> is <paramref name="target"/> or part of it: a child
    /// hosted in it (a WebView2 renderer runs in another process but is parented to
    /// the app window) or a popup it owns. Foreign top-level windows never qualify.
    /// </summary>
    public static bool BelongsTo(nint hwnd, nint target) => BelongsTo(hwnd, target, GetAncestor);

    private const uint GaRoot = 2;
    private const uint GaRootOwner = 3;

    internal static bool BelongsTo(nint hwnd, nint target, Func<nint, uint, nint> ancestor) =>
        hwnd != 0
        && target != 0
        && (hwnd == target || ancestor(hwnd, GaRoot) == target || ancestor(hwnd, GaRootOwner) == target);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassName(nint hWnd, StringBuilder lpClassName, int nMaxCount);

    /// <summary>Compact diagnostic identity for a window: handle, process id and class name.</summary>
    public static string Describe(nint hwnd)
    {
        if (hwnd == 0)
        {
            return "hwnd=0";
        }

        var name = new StringBuilder(256);
        GetClassName(hwnd, name, name.Capacity);
        return $"hwnd={hwnd}, pid={GetProcessId(hwnd)}, class={name}";
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmGetWindowAttribute(nint hWnd, int dwAttribute, out int pvAttribute, int cbAttribute);

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    /// <summary>
    /// Enumerate visible top-level windows, optionally restricted to a single process.
    /// </summary>
    /// <param name="processId">If set, only windows owned by this process are returned.</param>
    public static IReadOnlyList<Win32WindowInfo> GetTopLevelWindows(int? processId = null)
    {
        var result = new List<Win32WindowInfo>();
        EnumWindows(
            (hwnd, _) =>
            {
                if (!IsWindowVisible(hwnd))
                {
                    return true;
                }

                GetWindowThreadProcessId(hwnd, out var pid);
                if (processId.HasValue && pid != (uint)processId.Value)
                {
                    return true;
                }

                var titleBuilder = new StringBuilder(512);
                GetWindowText(hwnd, titleBuilder, titleBuilder.Capacity);

                var exStyle = GetWindowLongPtr(hwnd, GWL_EXSTYLE).ToInt64();
                var isToolWindow = (exStyle & WS_EX_TOOLWINDOW) != 0;

                var isCloaked = false;
                if (DwmGetWindowAttribute(hwnd, DWMWA_CLOAKED, out var cloaked, sizeof(int)) == 0)
                {
                    isCloaked = cloaked != 0;
                }

                result.Add(
                    new Win32WindowInfo(
                        hwnd,
                        titleBuilder.ToString(),
                        (int)pid,
                        IsWindowEnabled(hwnd),
                        isToolWindow,
                        isCloaked
                    )
                );
                return true;
            },
            0
        );
        return result;
    }

    /// <summary>
    /// Bring a window to the foreground, restoring it first if minimized.
    /// </summary>
    public static void FocusWindow(nint hwnd)
    {
        if (GetForegroundWindow() == hwnd)
        {
            return;
        }

        if (IsIconic(hwnd))
        {
            ShowWindow(hwnd, SW_RESTORE);
        }

        SetForegroundWindow(hwnd);
        for (var i = 0; i < 10 && GetForegroundWindow() != hwnd; i++)
        {
            Thread.Sleep(25);
        }
    }

    /// <summary>
    /// Request a window to close by posting WM_CLOSE (non-blocking).
    /// </summary>
    public static void CloseWindow(nint hwnd)
    {
        PostMessage(hwnd, WM_CLOSE, 0, 0);
    }

    /// <summary>
    /// Get the process id owning the current foreground window, or 0 if there
    /// is no foreground window.
    /// </summary>
    public static int GetForegroundWindowProcessId()
    {
        var hwnd = GetForegroundWindow();
        if (hwnd == 0)
        {
            return 0;
        }

        GetWindowThreadProcessId(hwnd, out var pid);
        return (int)pid;
    }

    /// <summary>
    /// Get a window's bounding rectangle in screen coordinates, or null on failure.
    /// </summary>
    public static System.Drawing.Rectangle? GetWindowBounds(nint hwnd)
    {
        if (!GetWindowRect(hwnd, out var rect))
        {
            return null;
        }

        return System.Drawing.Rectangle.FromLTRB(rect.Left, rect.Top, rect.Right, rect.Bottom);
    }

    [DllImport("dwmapi.dll", EntryPoint = "DwmGetWindowAttribute")]
    private static extern int DwmGetWindowRectAttribute(
        nint hWnd,
        int dwAttribute,
        out RECT pvAttribute,
        int cbAttribute
    );

    private const int DWMWA_EXTENDED_FRAME_BOUNDS = 9;

    /// <summary>
    /// A window's <em>visible</em> rectangle. <see cref="GetWindowBounds"/> reports what
    /// GetWindowRect returns, which on Windows 10/11 includes an invisible resize border of several
    /// pixels on each side. Two windows snapped side by side therefore overlap by about 8px in that
    /// space while nothing is visually covered, so occlusion must be judged on this rectangle
    /// instead. Falls back to GetWindowRect where DWM has no answer.
    /// </summary>
    /// <param name="hwnd">The window to measure.</param>
    /// <returns>The visible frame, or null when neither source can be read.</returns>
    public static System.Drawing.Rectangle? GetVisibleBounds(nint hwnd) =>
        DwmGetWindowRectAttribute(hwnd, DWMWA_EXTENDED_FRAME_BOUNDS, out var frame, Marshal.SizeOf<RECT>()) == 0
            ? System.Drawing.Rectangle.FromLTRB(frame.Left, frame.Top, frame.Right, frame.Bottom)
            : GetWindowBounds(hwnd);

    /// <summary>Smallest overlap with a capture that can meaningfully corrupt it.</summary>
    public const int MinimumObstructionSize = 8;

    /// <summary>
    /// Find the windows covering part of <paramref name="rect"/> at the moment
    /// <paramref name="zOrder"/> was taken. Screen-pixel capture grabs whatever is on the glass, so
    /// anything listed here lands in the image. Pure so it can be tested without a
    /// desktop; callers supply the observation.
    /// </summary>
    /// <param name="zOrder">Visible top-level windows in z-order, front to back.</param>
    /// <param name="target">The window being captured.</param>
    /// <param name="targetProcessId">Its process; that process's own dialogs and menus are allowed.</param>
    /// <param name="rect">The screen rectangle whose pixels are being read.</param>
    /// <returns>What was covering the rectangle, and whether the target was even found.</returns>
    public static OcclusionCheck FindObstructions(
        IReadOnlyList<WindowBounds> zOrder,
        nint target,
        int targetProcessId,
        System.Drawing.Rectangle rect
    )
    {
        var obstructions = new List<Win32WindowInfo>();
        foreach (var entry in zOrder)
        {
            // Everything before the target is above it; at the target, stop.
            if (entry.Window.Hwnd == target)
            {
                return new(true, obstructions);
            }

            if (entry.Window.IsCloaked)
            {
                continue;
            }
            // Minimized and click-through windows are in the z-order but not on the glass.
            if (!entry.CanCover)
            {
                continue;
            }
            // An owned dialog, menu or popup of the same app is usually the subject, not an intruder.
            if (targetProcessId != 0 && entry.Window.ProcessId == targetProcessId)
            {
                continue;
            }
            // An unreadable rectangle is not evidence of absence: treat it as covering.
            if (entry.Bounds is not { } bounds)
            {
                obstructions.Add(entry.Window);
                continue;
            }
            // Judge the overlap, not the window. A maximized window sharing an 8px edge with the
            // capture hides nothing, and testing the window's own size let it through only when the
            // window itself was tiny — which is the rarer case.
            var overlap = System.Drawing.Rectangle.Intersect(bounds, rect);
            if (overlap.Width < MinimumObstructionSize || overlap.Height < MinimumObstructionSize)
            {
                continue;
            }

            obstructions.Add(entry.Window);
        }
        // The target was never reached, so "above it" was never bounded. Fail closed.
        return new(false, obstructions);
    }

    /// <summary>
    /// Observe the current z-order with each window's visible bounds, for
    /// <see cref="FindObstructions"/>. A window that disappears between enumeration and measurement
    /// is dropped rather than recorded with unknown bounds: transient shell windows come and go
    /// constantly, and treating a window that no longer exists as opaque refuses good captures.
    /// </summary>
    /// <returns>Visible top-level windows, front to back, each with its visible bounds.</returns>
    public static IReadOnlyList<WindowBounds> ObserveZOrder() =>
        GetTopLevelWindows()
            .Select(w => new WindowBounds(w, GetVisibleBounds(w.Hwnd), CanCover(w.Hwnd)))
            .Where(entry => entry.Bounds != null || IsWindowVisible(entry.Window.Hwnd))
            .ToArray();

    /// <summary>
    /// Whether a window can put pixels over another. Minimized windows are still enumerated and
    /// DWMWA_EXTENDED_FRAME_BOUNDS reports their *restored* rectangle, so measuring one produces a
    /// full-size obstruction that is not on screen at all. Click-through overlays (WS_EX_TRANSPARENT,
    /// or layered at zero alpha) are on the glass but show what is behind them.
    /// </summary>
    internal static bool CanCover(nint hwnd)
    {
        if (IsIconic(hwnd))
        {
            return false;
        }

        var exStyle = GetWindowLongPtr(hwnd, GWL_EXSTYLE).ToInt64();
        if ((exStyle & WS_EX_TRANSPARENT) != 0)
        {
            return false;
        }

        if (
            (exStyle & WS_EX_LAYERED) != 0
            && GetLayeredWindowAttributes(hwnd, out _, out var alpha, out var flags)
            && (flags & LWA_ALPHA) != 0
            && alpha == 0
        )
        {
            return false;
        }

        return true;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct GUITHREADINFO
    {
        public int cbSize;
        public int flags;
        public nint hwndActive;
        public nint hwndFocus;
        public nint hwndCapture;
        public nint hwndMenuOwner;
        public nint hwndMoveSize;
        public nint hwndCaret;
        public RECT rcCaret;
    }

    [DllImport("user32.dll")]
    private static extern bool GetGUIThreadInfo(uint idThread, ref GUITHREADINFO lpgui);

    /// <summary>Get a window's class name, or an empty string on failure.</summary>
    public static string GetWindowClassName(nint hwnd)
    {
        var builder = new StringBuilder(256);
        return GetClassName(hwnd, builder, builder.Capacity) > 0 ? builder.ToString() : string.Empty;
    }

    /// <summary>Get a window's title, or an empty string on failure.</summary>
    public static string GetWindowTitle(nint hwnd)
    {
        var builder = new StringBuilder(512);
        return GetWindowText(hwnd, builder, builder.Capacity) > 0 ? builder.ToString() : string.Empty;
    }

    /// <summary>
    /// The control that currently holds keyboard focus on the foreground thread.
    /// This is a <em>window</em> handle, not a UI Automation element: a surface that hosts its
    /// content in a single HWND (WPF, for example) reports the whole window and discriminates
    /// nothing. Native child controls (Win32, WinForms, Scintilla) do report individually.
    /// Reads window-station state only, so it is safe while a UIA provider is blocked.
    /// </summary>
    /// <returns>The focused control, or null when no foreground thread reports focus.</returns>
    public static FocusedControl? GetFocusedControl()
    {
        var info = new GUITHREADINFO { cbSize = Marshal.SizeOf<GUITHREADINFO>() };
        // Thread id 0 means "the foreground thread", which is the target of ref-less input.
        if (!GetGUIThreadInfo(0, ref info) || info.hwndFocus == 0)
        {
            return null;
        }

        var root = GetAncestor(info.hwndFocus, 2);
        return new FocusedControl(
            info.hwndFocus,
            GetWindowClassName(info.hwndFocus),
            root,
            root == 0 ? string.Empty : GetWindowTitle(root),
            GetProcessId(info.hwndFocus)
        );
    }

    /// <summary>
    /// A window paired with its bounds, as one observation. Bounds are read separately from
    /// enumeration, so null means the read failed and the window must be treated as opaque.
    /// </summary>
    /// <param name="Window">The window.</param>
    /// <param name="Bounds">Its screen rectangle, or null when it could not be read.</param>
    /// <param name="Window">The enumerated window.</param>
    /// <param name="Bounds">Its visible rectangle, or null when it could not be measured.</param>
    /// <param name="CanCover">Whether it can actually put pixels over what is beneath it.</param>
    public sealed record WindowBounds(Win32WindowInfo Window, System.Drawing.Rectangle? Bounds, bool CanCover = true);

    /// <summary>
    /// Result of checking whether a rectangle was visually clean at capture time.
    /// </summary>
    /// <param name="TargetFound">Whether the target appeared in the z-order at all.</param>
    /// <param name="Obstructions">Windows above the target that intersect the rectangle.</param>
    public sealed record OcclusionCheck(bool TargetFound, IReadOnlyList<Win32WindowInfo> Obstructions)
    {
        /// <summary>Whether the rectangle is provably clean. Unknown counts as not clean.</summary>
        public bool IsClean => TargetFound && Obstructions.Count == 0;
    }
}

/// <summary>
/// The control holding keyboard focus, as the window station reports it.
/// </summary>
/// <param name="Hwnd">Focused window handle.</param>
/// <param name="ClassName">Window class of the focused handle.</param>
/// <param name="RootHwnd">Top-level window containing it.</param>
/// <param name="RootTitle">Title of that top-level window (may be empty).</param>
/// <param name="ProcessId">Owning process id.</param>
public sealed record FocusedControl(nint Hwnd, string ClassName, nint RootHwnd, string RootTitle, int ProcessId);
