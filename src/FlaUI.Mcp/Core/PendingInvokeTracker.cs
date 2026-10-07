namespace FlaUI.Mcp.Core;

/// <summary>
/// Information about a UI Automation call that has not returned yet.
/// While such a call is in flight, the target process's UIA provider typically
/// cannot answer any other request, so tools should fail fast instead of hanging.
/// </summary>
public sealed class PendingInvokeInfo
{
    internal PendingInvokeInfo(int processId, string description, long startedTicks)
    {
        ProcessStartedTicks = startedTicks;
        ProcessId = processId;
        Description = description;
        StartedUtc = DateTime.UtcNow;
    }

    public string OperationId { get; } = Guid.NewGuid().ToString("N");
    public string? ParentOperationId { get; } = OperationContext.Current.Value?.Id;
    public long ProcessStartedTicks { get; }

    /// <summary>Process id whose UIA provider is occupied by this call.</summary>
    public int ProcessId { get; }

    /// <summary>Human-readable description of the call, e.g. "Invoke on 'Open...'".</summary>
    public string Description { get; }

    public DateTime StartedUtc { get; }

    /// <summary>Title of the modal window detected after the call started, if any.</summary>
    public string? ModalTitle { get; set; }
}

/// <summary>Thrown when a lookup targets an app whose UIA provider is held by a pending call.</summary>
public sealed class ProviderBlockedException(PendingInvokeInfo pending)
    : InvalidOperationException(PendingInvokeTracker.DescribeBlocked(pending))
{
    public PendingInvokeInfo Pending { get; } = pending;
}

/// <summary>
/// Tracks UI Automation calls that are still executing per process, so other tools
/// can detect a blocked provider and fail immediately instead of waiting for a timeout.
/// </summary>
public class PendingInvokeTracker
{
    private readonly object _lock = new();
    private readonly List<PendingInvokeInfo> _pending = new();
    private readonly Func<int, long?> _processStart;

    public PendingInvokeTracker()
        : this(ReadProcessStart) { }

    internal PendingInvokeTracker(Func<int, long?> processStart) => _processStart = processStart;

    // null means exited/missing; zero means identity could not be inspected.
    private static long? ReadProcessStart(int pid)
    {
        try
        {
            using var process = System.Diagnostics.Process.GetProcessById(pid);
            return process.HasExited ? null : process.StartTime.ToUniversalTime().Ticks;
        }
        catch (ArgumentException)
        {
            return null;
        }
        catch (InvalidOperationException)
        {
            return null;
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return 0;
        }
    }

    public object Status()
    {
        lock (_lock)
        {
            return _pending
                .Select(p => new
                {
                    operationId = p.OperationId,
                    parentOperationId = p.ParentOperationId,
                    p.ProcessId,
                    p.ProcessStartedTicks,
                    p.Description,
                    p.StartedUtc,
                    p.ModalTitle,
                    status = "pending",
                })
                .ToArray();
        }
    }

    public string[] ForOperation(string operationId)
    {
        lock (_lock)
        {
            return _pending.Where(p => p.ParentOperationId == operationId).Select(p => p.OperationId).ToArray();
        }
    }

    public PendingInvokeInfo Begin(int processId, string description)
    {
        var info = new PendingInvokeInfo(processId, description, _processStart(processId) ?? 0);
        lock (_lock)
        {
            _pending.Add(info);
        }

        return info;
    }

    public void Complete(PendingInvokeInfo info)
    {
        lock (_lock)
        {
            _pending.Remove(info);
        }
    }

    /// <summary>Whether a process has a call still in flight. Process id 0 (unknown) never matches.</summary>
    public bool TryGetPending(int processId, out PendingInvokeInfo info)
    {
        // Process inspection must not hold the shared pending-list lock.
        var started = processId == 0 ? 0 : _processStart(processId);
        lock (_lock)
        {
            // A restarted process with the same id is not blocked by the old call.
            _pending.RemoveAll(p =>
                p.ProcessId == processId
                && p.ProcessStartedTicks != 0
                && (started == null || started > 0 && started != p.ProcessStartedTicks)
            );
            var match = processId != 0 ? _pending.FirstOrDefault(p => p.ProcessId == processId) : null;
            info = match!;
            return match != null;
        }
    }

    public static string DescribeBlocked(PendingInvokeInfo info)
    {
        var modal = info.ModalTitle != null ? $"; it opened modal \"{info.ModalTitle}\"" : "";
        var elapsed = (int)(DateTime.UtcNow - info.StartedUtc).TotalSeconds;
        return $"UI Automation for this app is blocked by pending operation {info.OperationId} ('{info.Description}', {elapsed}s){modal}. "
            + "Ref tools on this app fail until it returns. Find the dialog with windows_list_windows, then use windows_screenshot "
            + "or windows_send_keys (no ref) with its handle, and check windows_operation_status before continuing.";
    }
}
