using System.Runtime.InteropServices;

namespace FlaUI.Mcp.Core;

/// <summary>
/// Holds a resource (typically a Windows power availability request) while tool
/// calls are actively arriving, releasing it after a sliding idle period.
///
/// <see cref="Poke"/> acquires the resource on first call and extends the hold
/// on every subsequent call; when no poke arrives for the hold duration, the
/// resource is released so the machine returns to its normal power/lock policy.
/// </summary>
public sealed class KeepAwake : IDisposable
{
    private readonly object _lock = new();
    private readonly TimeSpan _holdDuration;
    private readonly Action _acquire;
    private readonly Action _release;
    private readonly IDisposable? _ownedResource;
    private readonly System.Threading.Timer _timer;
    private long _deadlineTicks;
    private bool _active;
    private bool _disposed;

    public KeepAwake(TimeSpan holdDuration, Action acquire, Action release, IDisposable? ownedResource = null)
    {
        if (holdDuration <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(holdDuration), "Hold duration must be greater than zero.");
        }

        _holdDuration = holdDuration;
        _acquire = acquire;
        _release = release;
        _ownedResource = ownedResource;
        _timer = new System.Threading.Timer(
            _ => OnTimerFired(),
            null,
            Timeout.InfiniteTimeSpan,
            Timeout.InfiniteTimeSpan
        );
    }

    /// <summary>
    /// Create a KeepAwake that tells Windows the display is in use (the same
    /// signal video players and conferencing apps send), preventing display
    /// timeout, sleep and the idle-triggered lock screen while tools are active.
    /// Returns null if the power request could not be created.
    /// </summary>
    public static KeepAwake? CreateDisplayKeepAwake(TimeSpan holdDuration, string reason)
    {
        var request = PowerAvailabilityRequest.Create(reason);
        if (request == null)
        {
            return null;
        }

        return new KeepAwake(holdDuration, request.Set, request.Clear, request);
    }

    public bool IsActive
    {
        get
        {
            lock (_lock)
            {
                return _active;
            }
        }
    }

    /// <summary>
    /// Signal activity: acquire the resource if not already held, and extend
    /// the hold so it is released only after the idle period elapses.
    /// </summary>
    public void Poke()
    {
        lock (_lock)
        {
            if (_disposed)
            {
                return;
            }

            if (!_active)
            {
                _acquire();
                _active = true;
            }

            _deadlineTicks = Environment.TickCount64 + (long)_holdDuration.TotalMilliseconds;
            _timer.Change(_holdDuration, Timeout.InfiniteTimeSpan);
        }
    }

    private void OnTimerFired()
    {
        lock (_lock)
        {
            if (_disposed || !_active)
            {
                return;
            }

            // A Poke may have raced with this callback; only release once the
            // most recently extended deadline has actually passed.
            var remainingMs = _deadlineTicks - Environment.TickCount64;
            if (remainingMs > 0)
            {
                _timer.Change(TimeSpan.FromMilliseconds(remainingMs), Timeout.InfiniteTimeSpan);
                return;
            }

            _release();
            _active = false;
        }
    }

    public void Dispose()
    {
        lock (_lock)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _timer.Dispose();
            if (_active)
            {
                try
                {
                    _release();
                }
                catch { }

                _active = false;
            }

            _ownedResource?.Dispose();
        }
    }
}

/// <summary>
/// Wraps a Windows power availability request (PowerCreateRequest) that, while
/// set, tells the OS the display is required. This suppresses display timeout,
/// automatic sleep and the inactivity lock screen - the same mechanism browsers
/// use during video playback. The request is visible in `powercfg /requests`
/// together with the reason string.
/// </summary>
public sealed class PowerAvailabilityRequest : IDisposable
{
    private readonly nint _handle;
    private bool _disposed;

    private PowerAvailabilityRequest(nint handle)
    {
        _handle = handle;
    }

    /// <summary>
    /// Create a power request with the given diagnostic reason, or null on failure.
    /// </summary>
    public static PowerAvailabilityRequest? Create(string reason)
    {
        var reasonPtr = Marshal.StringToHGlobalUni(reason);
        try
        {
            var context = new REASON_CONTEXT
            {
                Version = POWER_REQUEST_CONTEXT_VERSION,
                Flags = POWER_REQUEST_CONTEXT_SIMPLE_STRING,
                SimpleReasonString = reasonPtr,
            };

            var handle = PowerCreateRequest(ref context);
            if (handle == 0 || handle == INVALID_HANDLE_VALUE)
            {
                return null;
            }

            return new PowerAvailabilityRequest(handle);
        }
        finally
        {
            Marshal.FreeHGlobal(reasonPtr);
        }
    }

    /// <summary>Activate the request: display must stay on, system must stay awake.</summary>
    public void Set()
    {
        PowerSetRequest(_handle, POWER_REQUEST_TYPE.PowerRequestDisplayRequired);
        PowerSetRequest(_handle, POWER_REQUEST_TYPE.PowerRequestSystemRequired);
    }

    /// <summary>Deactivate the request, restoring normal power/lock policy.</summary>
    public void Clear()
    {
        PowerClearRequest(_handle, POWER_REQUEST_TYPE.PowerRequestDisplayRequired);
        PowerClearRequest(_handle, POWER_REQUEST_TYPE.PowerRequestSystemRequired);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        try
        {
            Clear();
        }
        catch { }

        CloseHandle(_handle);
    }

    private const uint POWER_REQUEST_CONTEXT_VERSION = 0;
    private const uint POWER_REQUEST_CONTEXT_SIMPLE_STRING = 0x1;
    private static readonly nint INVALID_HANDLE_VALUE = -1;

    private enum POWER_REQUEST_TYPE
    {
        PowerRequestDisplayRequired = 0,
        PowerRequestSystemRequired = 1,
        PowerRequestAwayModeRequired = 2,
        PowerRequestExecutionRequired = 3,
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct REASON_CONTEXT
    {
        public uint Version;
        public uint Flags;
        public nint SimpleReasonString;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern nint PowerCreateRequest(ref REASON_CONTEXT context);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool PowerSetRequest(nint powerRequest, POWER_REQUEST_TYPE requestType);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool PowerClearRequest(nint powerRequest, POWER_REQUEST_TYPE requestType);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(nint handle);
}
