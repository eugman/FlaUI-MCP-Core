using FlaUI.Core;
using FlaUI.Core.AutomationElements;
using FlaUI.UIA3;

namespace FlaUI.Mcp.Core;

/// <summary>
/// Manages UI Automation sessions and launched applications
/// </summary>
public class SessionManager : IDisposable
{
    private readonly UIA3Automation? _automation;
    private readonly object _gate = new();
    private bool _disposed;
    private readonly Dictionary<string, Window> _windows = new();
    private readonly Dictionary<string, nint> _windowHwnds = new();
    private readonly Dictionary<string, int> _windowPids = new();
    private readonly Dictionary<nint, string> _hwndToHandle = new();
    private readonly ProcessPolicy _processPolicy;
    private int _windowCounter = 0;
    private readonly Dictionary<string, InputTarget> _identities = new();

    public InputTarget GetInputTarget(string handle)
    {
        InputTarget target;
        lock (_gate)
        {
            target = _identities.TryGetValue(handle, out var value)
                ? value
                : throw new ArgumentException("Unknown input target.");
        }

        target.EnsureAlive();
        return target;
    }

    public ProcessIdentity? ResolveTarget(System.Text.Json.JsonElement? args, ElementRegistry refs)
    {
        // Diagnostic metadata only: no provider/process reads and no input validation.
        if (args is not { ValueKind: System.Text.Json.JsonValueKind.Object } a || a.TryGetProperty("actions", out _))
        {
            return null;
        }

        string? handle = null;
        if (a.TryGetProperty("handle", out var h) && h.ValueKind == System.Text.Json.JsonValueKind.String)
        {
            handle = h.GetString();
        }
        else if (
            a.TryGetProperty("ref", out var r)
            && r.ValueKind == System.Text.Json.JsonValueKind.String
            && refs.HasElement(r.GetString()!)
        )
        {
            handle = refs.WindowForRef(r.GetString()!);
        }

        lock (_gate)
        {
            return handle != null && _identities.TryGetValue(handle, out var identity)
                ? new(identity.ProcessId, identity.StartedTicks)
                : null;
        }
    }

    public SessionManager(ProcessPolicy? processPolicy = null)
        : this(new UIA3Automation(), processPolicy ?? ProcessPolicy.AllowAll) { }

    // A null automation supports metadata-only tests without constructing a native provider.
    internal SessionManager(UIA3Automation? automation, ProcessPolicy processPolicy)
    {
        _automation = automation;
        _processPolicy = processPolicy;
    }

    public UIA3Automation Automation =>
        _automation ?? throw new InvalidOperationException("No UI Automation provider configured.");

    public (string handle, Window window) LaunchApp(string appPath, string[]? args = null)
    {
        if (!_processPolicy.IsExecutableAllowed(appPath))
        {
            throw new Exception(_processPolicy.DescribeDenied($"'{appPath}'"));
        }

        // Use Process.Start for more reliable launching
        var psi = LaunchStartInfo(appPath, args);

        OperationContext.Check();
        using var process = System.Diagnostics.Process.Start(psi);
        if (process == null)
        {
            throw new Exception($"Failed to start process: {appPath}");
        }

        // Wait for the process to be ready
        try
        {
            process.WaitForInputIdle(5000);
        }
        catch
        { /* Some processes don't support this */
        }

        Thread.Sleep(1000); // Extra wait for window to appear
        Window? window = null;
        var timeouts = 0;
        IReadOnlyList<Win32WindowInfo> Candidates() =>
            Win32Desktop.GetTopLevelWindows(process.Id).Where(w => !w.IsToolWindow && !w.IsCloaked).ToArray();
        // A title or an unregistered window is not proof of launch ownership.
        // Brokered/single-instance launches must be attached explicitly by the caller.
        for (var attempt = 0; attempt < 10 && window == null && timeouts < 2; attempt++)
        {
            OperationContext.Check();
            if (process.HasExited)
            {
                break;
            }

            var candidates = Candidates();
            if (candidates.Count == 1)
            {
                // A provider still starting up can time out; each timeout already waited, so stop after two.
                try
                {
                    window = Automation.FromHandle(candidates[0].Hwnd)?.AsWindow();
                }
                catch (TimeoutException)
                {
                    timeouts++;
                }

                OperationContext.Check();
            }

            if (window == null)
            {
                Thread.Sleep(500);
            }
        }

        if (window == null)
        {
            var exited = process.HasExited;
            var titles =
                exited || !_processPolicy.IsProcessAllowed(process.Id)
                    ? Array.Empty<string>()
                    : Candidates().Select(w => w.Title).Where(t => t.Length > 0).ToArray();
            throw new Exception(LaunchFailure(appPath, process.Id, exited, titles, timeouts > 0));
        }

        var windowHandle = RegisterWindow(window);
        return (windowHandle, window);
    }

    internal static string LaunchFailure(
        string appPath,
        int processId,
        bool exited,
        IReadOnlyList<string> titles,
        bool timedOut = false
    ) =>
        exited
            ? $"Launch was requested for {appPath} (PID {processId}), but that process has exited; another instance may own the window. Do not blindly relaunch. Use windows_list_windows and explicitly attach to the intended window."
            : $"Launch was requested for {appPath} (PID {processId}), but "
                + (
                    timedOut
                        ? "its window did not respond to UI Automation in time"
                        : "no unique owned window was found"
                )
                + (titles.Count > 0 ? $"; its windows are {string.Join(", ", titles.Select(t => $"\"{t}\""))}" : "")
                + ". Do not blindly relaunch. Use windows_list_windows and explicitly attach to the intended window.";

    internal static System.Diagnostics.ProcessStartInfo LaunchStartInfo(string appPath, string[]? args)
    {
        var info = new System.Diagnostics.ProcessStartInfo(appPath) { UseShellExecute = true };
        foreach (var argument in args ?? [])
        {
            info.ArgumentList.Add(argument);
        }

        return info;
    }

    public (string handle, Window window) AttachToWindow(string title)
    {
        OperationContext.Check();
        // Match top-level titles through Win32; a desktop-wide UIA search can hang on unrelated apps.
        var match = Win32Desktop.GetTopLevelWindows().FirstOrDefault(w => w.Title == title && !w.IsCloaked);
        var window = match == null ? null : Automation.FromHandle(match.Hwnd)?.AsWindow();

        if (window == null)
        {
            throw new Exception($"Window not found: {title}");
        }

        var handle = RegisterWindow(window);
        return (handle, window);
    }

    public string RegisterWindow(Window window)
    {
        OperationContext.Check();
        // Capture the native handle and process id while the provider is
        // responsive, so later operations (focus, close, blocked-provider
        // checks) can work without any UI Automation round-trips.
        nint hwnd = 0;
        var pid = 0;
        try
        {
            hwnd = window.Properties.NativeWindowHandle.ValueOrDefault;
            pid = window.Properties.ProcessId.ValueOrDefault;
        }
        catch
        { /* best effort */
        }

        if (hwnd != 0 && pid == 0)
        {
            pid = Win32Desktop.GetProcessId(hwnd);
        }

        EnsureProcessAllowed(pid);

        // Capture before publishing even when re-registering an existing HWND.
        var identity = hwnd != 0 ? InputTarget.Capture(hwnd, pid) : null;
        return PublishWindow(window, hwnd, pid, identity);
    }

    /// <summary>
    /// Register a window by its native handle only, without touching UI Automation.
    /// The UIA Window object is created lazily on first use in <see cref="GetWindow"/>.
    /// </summary>
    public string RegisterNativeWindow(nint hwnd, int processId)
    {
        OperationContext.Check();
        EnsureProcessAllowed(processId);

        var identity = InputTarget.Capture(hwnd, processId);
        return PublishWindow(null, hwnd, processId, identity);
    }

    internal string PublishWindow(Window? window, nint hwnd, int processId, InputTarget? identity)
    {
        // Native/provider reads must finish before entering the metadata gate.
        lock (_gate)
        {
            OperationContext.Check();
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (
                hwnd != 0
                && _hwndToHandle.TryGetValue(hwnd, out var existing)
                && _identities.TryGetValue(existing, out var oldIdentity)
                && oldIdentity == identity
            )
            {
                if (window != null)
                {
                    _windows[existing] = window;
                }

                if (processId != 0)
                {
                    _windowPids[existing] = processId;
                }

                return existing;
            }

            var handle = $"w{++_windowCounter}";
            if (window != null)
            {
                _windows[handle] = window;
            }

            if (identity != null)
            {
                _identities[handle] = identity;
            }

            if (hwnd != 0)
            {
                _windowHwnds[handle] = hwnd;
                _hwndToHandle[hwnd] = handle;
            }

            if (processId != 0)
            {
                _windowPids[handle] = processId;
            }

            return handle;
        }
    }

    public Window? GetWindow(string handle) =>
        GetWindow(handle, hwnd => Automation.FromHandle(hwnd)?.AsWindow(), identity => identity.EnsureAlive());

    internal Window? GetWindow(string handle, Func<nint, Window?> attach, Action<InputTarget> validateIdentity)
    {
        OperationContext.Check();
        InputTarget? identity;
        Window? window;
        nint hwnd;
        lock (_gate)
        {
            identity = _identities.GetValueOrDefault(handle);
            window = _windows.GetValueOrDefault(handle);
            hwnd = _windowHwnds.GetValueOrDefault(handle);
        }

        if (identity != null)
        {
            try
            {
                validateIdentity(identity);
            }
            catch (ArgumentException)
            {
                ForgetWindow(handle);
                return null;
            }
            catch (InvalidOperationException)
            {
                ForgetWindow(handle);
                return null;
            }
            catch (System.ComponentModel.Win32Exception)
            {
                ForgetWindow(handle);
                return null;
            }
        }

        OperationContext.Check();
        if (window != null)
        {
            lock (_gate)
            {
                return ReferenceEquals(_windows.GetValueOrDefault(handle), window) ? window : null;
            }
        }

        // Lazily attach to windows registered via RegisterNativeWindow
        if (hwnd != 0)
        {
            var attached = attach(hwnd);
            lock (_gate)
            {
                OperationContext.Check();
                // Recovery may have forgotten this registration during the provider call.
                if (
                    attached == null
                    || _disposed
                    || _windowHwnds.GetValueOrDefault(handle) != hwnd
                    || _identities.GetValueOrDefault(handle) != identity
                )
                {
                    return null;
                }

                _windows[handle] = attached;
                return attached;
            }
        }

        return null;
    }

    /// <summary>
    /// Get the cached process id for a window handle, or 0 if unknown.
    /// Never touches UI Automation.
    /// </summary>
    public int GetWindowProcessId(string handle)
    {
        lock (_gate)
        {
            return _windowPids.TryGetValue(handle, out var pid) ? pid : 0;
        }
    }

    /// <summary>
    /// Get the cached native window handle for a window handle, or 0 if unknown.
    /// Never touches UI Automation.
    /// </summary>
    public nint GetWindowHwnd(string handle)
    {
        lock (_gate)
        {
            return _windowHwnds.TryGetValue(handle, out var hwnd) ? hwnd : 0;
        }
    }

    /// <summary>
    /// List top-level windows using Win32 enumeration only. This never blocks,
    /// even when an app's UI Automation provider is busy (e.g., held up by a
    /// modal dialog opened from a pending Invoke call).
    /// </summary>
    public List<(string handle, string title, string? processName)> ListWindows()
    {
        (string Handle, nint Hwnd, int Pid)[] registered;
        lock (_gate)
        {
            registered = _windowHwnds
                .Select(pair => (pair.Key, pair.Value, _windowPids.GetValueOrDefault(pair.Key)))
                .ToArray();
        }

        foreach (var (handle, hwnd, pid) in registered)
        {
            if (Win32Desktop.GetProcessId(hwnd) != pid)
            {
                ForgetWindow(handle);
            }
        }

        var result = new List<(string, string, string?)>();
        foreach (var info in Win32Desktop.GetTopLevelWindows())
        {
            if (info.Title.Length == 0 || info.IsToolWindow || info.IsCloaked)
            {
                continue;
            }

            var processName = ProcessPolicy.TryGetProcessName(info.ProcessId);

            // When an allowlist is active, windows of other apps are not listed
            // at all - no handle is registered, so they stay unreachable.
            if (!_processPolicy.IsNameAllowed(processName))
            {
                continue;
            }

            try
            {
                var handle = RegisterNativeWindow(info.Hwnd, info.ProcessId);
                result.Add((handle, info.Title, processName));
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (System.ComponentModel.Win32Exception)
            {
                // Inaccessible processes are not controllable; continue listing others.
            }
            catch (ArgumentException)
            {
                // Process.GetProcessById can race process exit.
            }
            catch (Exception) when (Win32Desktop.GetProcessId(info.Hwnd) != info.ProcessId)
            {
                // Window closed or changed owner between enumeration and registration.
                // Keep other windows in the result; do not suppress unrelated failures.
            }
        }

        return result;
    }

    public void FocusWindow(string handle)
    {
        OperationContext.Check();
        // Prefer Win32 focus (never blocks); fall back to UIA for windows
        // registered before a native handle was captured.
        var hwnd = GetWindowHwnd(handle);
        if (hwnd != 0)
        {
            GetInputTarget(handle);
            Win32Desktop.FocusWindow(hwnd);
            return;
        }

        var window = GetWindow(handle);
        if (window == null)
        {
            throw new Exception($"Window not found: {handle}");
        }

        EnsureProcessAllowed(window.Properties.ProcessId.Value);
        OperationContext.Check();
        window.Focus();
    }

    public void CloseWindow(string handle)
    {
        OperationContext.Check();
        // Prefer a Win32 WM_CLOSE (never blocks); fall back to UIA.
        var hwnd = GetWindowHwnd(handle);
        if (hwnd != 0)
        {
            GetInputTarget(handle);
            Win32Desktop.CloseWindow(hwnd);
            // WM_CLOSE is asynchronous and may open a save prompt. Retain the
            // handle while the native window still exists so recovery can use it.
            if (Win32Desktop.GetProcessId(hwnd) != 0)
            {
                return;
            }
        }
        else
        {
            var window = GetWindow(handle);
            if (window == null)
            {
                throw new Exception($"Window not found: {handle}");
            }

            EnsureProcessAllowed(window.Properties.ProcessId.Value);
            OperationContext.Check();
            window.Close();
            // UIA Close is also a request, not proof of disappearance.
            return;
        }

        ForgetWindow(handle);
    }

    internal void ForgetWindow(string handle)
    {
        lock (_gate)
        {
            _windows.Remove(handle);
            _identities.Remove(handle);
            if (_windowHwnds.TryGetValue(handle, out var removedHwnd))
            {
                if (_hwndToHandle.GetValueOrDefault(removedHwnd) == handle)
                {
                    _hwndToHandle.Remove(removedHwnd);
                }

                _windowHwnds.Remove(handle);
            }

            _windowPids.Remove(handle);
        }
    }

    /// <summary>
    /// Throw when an app allowlist is active and the process is not on it (or
    /// cannot be identified). Every window-handle registration funnels through
    /// this, so refs and handles can only ever point at allowed apps.
    /// </summary>
    private void EnsureProcessAllowed(int processId)
    {
        if (!_processPolicy.IsRestricted)
        {
            return;
        }

        if (processId == 0)
        {
            throw new Exception(
                "Cannot verify this window's owning process against the app allowlist "
                    + $"({ProcessPolicy.EnvironmentVariable}), so it is not controllable."
            );
        }

        if (!_processPolicy.IsProcessAllowed(processId))
        {
            var name = ProcessPolicy.TryGetProcessName(processId) ?? $"pid {processId}";
            throw new Exception(_processPolicy.DescribeDenied($"Process '{name}'"));
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _windows.Clear();
            _identities.Clear();
            _windowHwnds.Clear();
            _windowPids.Clear();
            _hwndToHandle.Clear();
        }

        _automation?.Dispose();
    }
}
