using System.Diagnostics;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Input;

namespace FlaUI.Mcp.Core;

public sealed record InputTarget(nint Hwnd, int ProcessId, long StartedTicks, nint HitHwnd = 0)
{
    public void EnsureAlive()
    {
        using var process = Process.GetProcessById(ProcessId);
        if (process.StartTime.ToUniversalTime().Ticks != StartedTicks || Win32Desktop.GetProcessId(Hwnd) != ProcessId)
        {
            throw new InvalidOperationException("Stale process/window identity.");
        }
    }

    public static InputTarget Capture(nint hwnd, int pid)
    {
        if (hwnd == 0 || pid == 0 || Win32Desktop.GetProcessId(hwnd) != pid)
        {
            throw new InvalidOperationException("Cannot establish window ownership.");
        }

        using var process = Process.GetProcessById(pid);
        return new(hwnd, pid, process.StartTime.ToUniversalTime().Ticks);
    }
}

/// <summary>Physical input is global: serialize it and fail closed on target/focus drift.</summary>
public sealed class GuardedInput : IDisposable
{
    private readonly IDisposable lease;
    private readonly InputTarget target;

    public GuardedInput(InputTarget target, AutomationElement? element = null, bool verifyFocus = false)
    {
        this.target = target;
        OperationContext.Check();
        lease = AcquireLease();
        try
        {
            target.EnsureAlive();
            MutationGuard.CheckPermission();
            if (!Win32Desktop.BelongsTo(Win32Desktop.GetForegroundWindow(), target.Hwnd))
            {
                Win32Desktop.FocusWindow(target.Hwnd);
            }

            if (element != null)
            {
                element.Focus();
            }

            RequireActivated(target);
            Verify();
            if (
                verifyFocus
                && (element == null || !element.Properties.HasKeyboardFocus.TryGetValue(out var focused) || !focused)
            )
            {
                throw new InvalidOperationException(
                    "Intended control did not receive verified keyboard focus; no input sent."
                );
            }
        }
        catch
        {
            lease.Dispose();
            throw;
        }
    }

    private static readonly SemaphoreSlim InputGate = new(1, 1);

    public static IDisposable AcquireLease()
    {
        if (
            !InputGate.Wait(
                TimeSpan.FromSeconds(2),
                OperationContext.Current.Value?.Stop.Token ?? CancellationToken.None
            )
        )
        {
            throw new TimeoutException("Desktop input is busy; no input sent.");
        }

        return new InputLease();
    }

    private sealed class InputLease : IDisposable
    {
        public void Dispose() => InputGate.Release();
    }

    public static InputTarget ForegroundTarget(ProcessPolicy policy) =>
        ForegroundTarget(
            policy,
            () =>
            {
                var hwnd = Win32Desktop.GetForegroundWindow();
                return InputTarget.Capture(hwnd, Win32Desktop.GetProcessId(hwnd));
            }
        );

    internal static InputTarget ForegroundTarget(ProcessPolicy policy, Func<InputTarget> captureTarget)
    {
        var captured = captureTarget();
        // Authorize the captured identity, not an earlier foreground observation.
        if (!policy.IsProcessAllowed(captured.ProcessId))
        {
            var name = ProcessPolicy.TryGetProcessName(captured.ProcessId) ?? "unknown";
            throw new InvalidOperationException(
                policy.DescribeDenied($"The foreground window's process '{name}'")
                    + " Focus an allowed window first, or target an element ref directly."
            );
        }

        return captured;
    }

    private static void RequireActivated(InputTarget target)
    {
        var foreground = Win32Desktop.GetForegroundWindow();
        if (Win32Desktop.BelongsTo(foreground, target.Hwnd))
        {
            return;
        }

        var foregroundPid = foreground == 0 ? 0 : Win32Desktop.GetProcessId(foreground);
        throw new InvalidOperationException(
            DescribeActivationFailure(
                target.ProcessId,
                foreground,
                foregroundPid,
                foregroundPid == 0 ? null : ProcessPolicy.TryGetProcessName(foregroundPid)
            )
        );
    }

    // Windows can refuse SetForegroundWindow (foreground lock) and retrying cannot override it.
    // Only a user click or an uncontested interactive session can, so name the blocker.
    internal static string DescribeActivationFailure(
        int targetPid,
        nint foreground,
        int foregroundPid,
        string? foregroundName
    )
    {
        if (foreground == 0)
        {
            return "desktop-unavailable: Windows reports no foreground window; the session may be locked, disconnected or non-interactive. "
                + "No input sent. Observe again; unattended runs need an unlocked interactive session.";
        }

        if (foregroundPid != targetPid)
        {
            return $"activation-denied: Windows kept '{foregroundName ?? "unknown"}' (PID {foregroundPid}) in the foreground instead of target PID {targetPid}. "
                + "No input sent. Attended: ask the user to click the target window's title bar, then observe before retrying. Unattended: activation cannot be forced.";
        }

        return $"activation-denied: Windows kept another window (hwnd={foreground}) of target PID {targetPid} in the foreground. "
            + "No input sent. Focus the intended target window, then observe before retrying.";
    }

    public void Verify()
    {
        OperationContext.Check();
        MutationGuard.CheckPermission();
        var foreground = Win32Desktop.GetForegroundWindow();
        if (
            !ForegroundAcceptable(
                target,
                foreground,
                Win32Desktop.GetProcessId,
                Win32Desktop.BelongsTo,
                Win32Desktop.IsWindowEnabled
            )
        )
        {
            throw new InvalidOperationException("Input target/focus changed or is disabled; no further input sent.");
        }
    }

    // The target's own hosted children (e.g. a cross-process WebView2 renderer) and owned popups
    // count as the target; a separate top-level window does not, even in the same process.
    internal static bool ForegroundAcceptable(
        InputTarget target,
        nint foreground,
        Func<nint, int> processOf,
        Func<nint, nint, bool> belongsTo,
        Func<nint, bool> enabled
    )
    {
        if (processOf(target.Hwnd) != target.ProcessId || foreground == 0)
        {
            return false;
        }

        return belongsTo(foreground, target.Hwnd) && enabled(foreground);
    }

    public void Send(Action input)
    {
        Verify();
        input();
    }

    public void Type(string text)
    {
        // Recheck foreground between chunks, without repeated process-start queries.
        var chunk = new System.Text.StringBuilder(64);
        foreach (var rune in text.EnumerateRunes())
        {
            chunk.Append(rune);
            if (chunk.Length < 64)
            {
                continue;
            }

            Send(() => Keyboard.Type(chunk.ToString()));
            chunk.Clear();
        }

        if (chunk.Length > 0)
        {
            Send(() => Keyboard.Type(chunk.ToString()));
        }
    }

    public void Click(AutomationElement element, MouseButton button = MouseButton.Left, bool doubleClick = false)
    {
        var point = element.GetClickablePoint();
        Verify();
        var clickRoot = target.HitHwnd == 0 ? target.Hwnd : target.HitHwnd;
        if (Win32Desktop.GetProcessId(clickRoot) != target.ProcessId || Win32Desktop.WindowAt(point) != clickRoot)
        {
            throw new InvalidOperationException("Click point is obscured by another window.");
        }

        Send(() =>
        {
            if (doubleClick)
            {
                Mouse.DoubleClick(point, button);
            }
            else
            {
                Mouse.Click(point, button);
            }
        });
    }

    public void Dispose() => lease.Dispose();
}
