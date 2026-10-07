using System.Runtime.ExceptionServices;

namespace FlaUI.Mcp.Core;

/// <summary>
/// Outcome of a modal-aware pattern call.
/// </summary>
public enum PatternCallOutcome
{
    /// <summary>The pattern call returned within the grace period.</summary>
    Completed,

    /// <summary>A modal dialog opened; the pattern call is still executing in the background.</summary>
    ModalDetected,

    /// <summary>The pattern call did not return within the grace period and no modal was detected.</summary>
    StillPending,
}

/// <summary>
/// Result of a modal-aware pattern call.
/// </summary>
/// <param name="Outcome">What happened within the grace period.</param>
/// <param name="ModalTitle">Title of the detected modal window, if any.</param>
public sealed record PatternCallResult(
    PatternCallOutcome Outcome,
    string? ModalTitle = null,
    string? PendingPatternId = null
);

/// <summary>
/// Executes UI Automation pattern calls (Invoke, Toggle, Select, ...) so that a
/// handler which opens a modal dialog does not hang the calling tool.
///
/// Background: UIA pattern calls are synchronous cross-process calls. In WinForms
/// and many other frameworks, a button handler that calls ShowDialog() will not
/// return until the dialog closes — so the pattern call, and with it the entire
/// UIA provider of the target process, stays blocked for as long as the dialog
/// is open. This class runs the call on a background thread and watches the
/// target process's top-level windows via non-blocking Win32 APIs. When a new
/// window appears (or an existing window is disabled by a modal), it reports
/// ModalDetected right away so the caller can interact with the dialog instead
/// of timing out.
/// </summary>
public static class ModalAwareInvoker
{
    /// <summary>How long to wait for the pattern call before giving up on completion.</summary>
    public static readonly TimeSpan DefaultGracePeriod = TimeSpan.FromSeconds(2);

    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(50);

    /// <summary>
    /// Execute a pattern call with modal detection.
    /// </summary>
    /// <param name="processId">Target process id (0 if unknown; disables modal detection).</param>
    /// <param name="description">Human-readable description, e.g. "Invoke on 'OK'".</param>
    /// <param name="patternCall">The synchronous pattern call to execute.</param>
    /// <param name="tracker">Tracker that other tools consult to fail fast while the call is pending.</param>
    /// <param name="gracePeriod">Max time to wait for completion; defaults to <see cref="DefaultGracePeriod"/>.</param>
    /// <param name="windowEnumerator">Override for window enumeration (for testing).</param>
    /// <exception cref="Exception">Rethrows any exception from <paramref name="patternCall"/> when it completes within the grace period.</exception>
    public static PatternCallResult Execute(
        int processId,
        string description,
        Action patternCall,
        PendingInvokeTracker tracker,
        TimeSpan? gracePeriod = null,
        Func<int, IReadOnlyList<Win32WindowInfo>>? windowEnumerator = null
    )
    {
        OperationContext.Check();
        var grace = gracePeriod ?? DefaultGracePeriod;
        windowEnumerator ??= pid => Win32Desktop.GetTopLevelWindows(pid);

        var windowsBefore =
            processId != 0
                ? windowEnumerator(processId).ToDictionary(w => w.Hwnd)
                : new Dictionary<nint, Win32WindowInfo>();

        var info = tracker.Begin(processId, description);
        var task = Task.Run(() =>
        {
            OperationContext.Check();
            patternCall();
        });

        // Ensure the tracker is cleared whenever the call eventually returns,
        // even if we stop waiting for it below. Also observe any exception so
        // it does not surface as an unobserved task exception.
        _ = task.ContinueWith(
            t =>
            {
                _ = t.Exception;
                tracker.Complete(info);
            },
            TaskScheduler.Default
        );

        var deadline = DateTime.UtcNow + grace;
        while (true)
        {
            // WaitAny (unlike Task.Wait) does not throw for faulted tasks,
            // letting us rethrow the original exception un-wrapped below
            if (Task.WaitAny(new[] { task }, PollInterval) == 0)
            {
                OperationContext.Check();
                if (task.IsCanceled)
                {
                    throw new OperationCanceledException();
                }

                if (task.IsFaulted && task.Exception != null)
                {
                    var inner = task.Exception.InnerException ?? task.Exception;
                    ExceptionDispatchInfo.Capture(inner).Throw();
                }

                return new PatternCallResult(PatternCallOutcome.Completed);
            }

            if (processId != 0)
            {
                var modalTitle = DetectModal(windowsBefore, windowEnumerator(processId));
                if (modalTitle != null)
                {
                    info.ModalTitle = modalTitle;
                    return new PatternCallResult(PatternCallOutcome.ModalDetected, modalTitle, info.OperationId);
                }
            }

            if (DateTime.UtcNow >= deadline)
            {
                return new PatternCallResult(PatternCallOutcome.StillPending, PendingPatternId: info.OperationId);
            }
        }
    }

    /// <summary>
    /// Detect the modal signature: a new top-level window appeared, or a
    /// previously enabled window became disabled (modal dialogs disable their owner).
    /// Returns the best available title for the modal, or null if no modal is detected.
    /// </summary>
    private static string? DetectModal(
        IReadOnlyDictionary<nint, Win32WindowInfo> before,
        IReadOnlyList<Win32WindowInfo> current
    )
    {
        Win32WindowInfo? newWindow = null;
        var ownerDisabled = false;

        foreach (var window in current)
        {
            if (!before.TryGetValue(window.Hwnd, out var previous))
            {
                // Prefer a titled new window over an untitled one
                if (newWindow == null || (newWindow.Title.Length == 0 && window.Title.Length > 0))
                {
                    newWindow = window;
                }
            }
            else if (previous.IsEnabled && !window.IsEnabled)
            {
                ownerDisabled = true;
            }
        }

        if (newWindow != null)
        {
            return newWindow.Title.Length > 0 ? newWindow.Title : "(untitled window)";
        }

        return ownerDisabled ? "(unknown title)" : null;
    }
}
