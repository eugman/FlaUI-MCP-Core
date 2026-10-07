using System.Diagnostics;
using FlaUI.Mcp.Core;
using Xunit;

namespace FlaUI.Mcp.Tests;

public class ModalAwareInvokerTests
{
    private const int TestProcessId = 1234;

    private static Win32WindowInfo MakeWindow(nint hwnd, string title, bool enabled = true) =>
        new(hwnd, title, TestProcessId, enabled, IsToolWindow: false, IsCloaked: false);

    [Fact]
    public void Execute_CompletedAction_ReturnsCompleted_AndClearsTracker()
    {
        var tracker = new PendingInvokeTracker();
        var windows = new List<Win32WindowInfo> { MakeWindow(1, "Main") };

        var result = ModalAwareInvoker.Execute(
            TestProcessId,
            "Invoke on 'OK'",
            () => { },
            tracker,
            windowEnumerator: _ => windows
        );

        Assert.Equal(PatternCallOutcome.Completed, result.Outcome);

        // The tracker is cleared by a task continuation; allow it a moment
        AssertTrackerClearsWithin(tracker, TimeSpan.FromSeconds(2));
    }

    [Fact]
    public void Execute_FaultedAction_RethrowsException()
    {
        var tracker = new PendingInvokeTracker();

        var ex = Assert.Throws<InvalidOperationException>(() =>
            ModalAwareInvoker.Execute(
                TestProcessId,
                "Invoke on 'OK'",
                () => throw new InvalidOperationException("boom"),
                tracker,
                windowEnumerator: _ => new List<Win32WindowInfo>()
            )
        );

        Assert.Equal("boom", ex.Message);
        AssertTrackerClearsWithin(tracker, TimeSpan.FromSeconds(2));
    }

    [Fact]
    public void Execute_BlockingActionWithNewWindow_ReturnsModalDetected()
    {
        var tracker = new PendingInvokeTracker();
        using var release = new ManualResetEventSlim(false);
        var callCount = 0;

        // First enumeration: only the main window. Later enumerations: a new
        // dialog window appeared while the action is still blocking.
        IReadOnlyList<Win32WindowInfo> Enumerate(int _)
        {
            var windows = new List<Win32WindowInfo> { MakeWindow(1, "Main", enabled: callCount == 0) };
            if (Interlocked.Increment(ref callCount) > 1)
            {
                windows.Add(MakeWindow(2, "Test Modal Dialog"));
            }

            return windows;
        }

        try
        {
            var sw = Stopwatch.StartNew();
            var result = ModalAwareInvoker.Execute(
                TestProcessId,
                "Invoke on 'Open Modal'",
                () => release.Wait(),
                tracker,
                gracePeriod: TimeSpan.FromSeconds(10),
                windowEnumerator: Enumerate
            );
            sw.Stop();

            Assert.Equal(PatternCallOutcome.ModalDetected, result.Outcome);
            Assert.Equal("Test Modal Dialog", result.ModalTitle);
            Assert.True(sw.Elapsed < TimeSpan.FromSeconds(5), $"Modal detection too slow: {sw.Elapsed}");

            // The call is still pending and the tracker knows about the modal
            Assert.True(tracker.TryGetPending(TestProcessId, out var pending));
            Assert.Equal("Test Modal Dialog", pending.ModalTitle);
        }
        finally
        {
            release.Set();
        }

        // Once the action returns, the tracker clears
        AssertTrackerClearsWithin(tracker, TimeSpan.FromSeconds(2));
    }

    [Fact]
    public void Execute_BlockingActionOwnerDisabledOnly_ReturnsModalDetected()
    {
        var tracker = new PendingInvokeTracker();
        using var release = new ManualResetEventSlim(false);
        var callCount = 0;

        // The owner window becomes disabled but no new window is enumerated
        // (e.g. the dialog is a tool window filtered elsewhere).
        IReadOnlyList<Win32WindowInfo> Enumerate(int _)
        {
            var enabled = Interlocked.Increment(ref callCount) == 1;
            return new List<Win32WindowInfo> { MakeWindow(1, "Main", enabled) };
        }

        try
        {
            var elapsed = Stopwatch.StartNew();
            var result = ModalAwareInvoker.Execute(
                TestProcessId,
                "Invoke on 'Open Modal'",
                () => release.Wait(),
                tracker,
                gracePeriod: TimeSpan.FromSeconds(10),
                windowEnumerator: Enumerate
            );

            Assert.Equal(PatternCallOutcome.ModalDetected, result.Outcome);
            Assert.True(
                elapsed.Elapsed < TimeSpan.FromSeconds(5),
                "Disabled-owner detection waited for the grace period"
            );
        }
        finally
        {
            release.Set();
        }
    }

    [Fact]
    public void Execute_BlockingActionNoWindowChange_ReturnsStillPendingAfterGrace()
    {
        var tracker = new PendingInvokeTracker();
        using var release = new ManualResetEventSlim(false);
        var windows = new List<Win32WindowInfo> { MakeWindow(1, "Main") };

        try
        {
            var sw = Stopwatch.StartNew();
            var result = ModalAwareInvoker.Execute(
                TestProcessId,
                "Invoke on 'Slow'",
                () => release.Wait(),
                tracker,
                gracePeriod: TimeSpan.FromMilliseconds(300),
                windowEnumerator: _ => windows
            );
            sw.Stop();

            Assert.Equal(PatternCallOutcome.StillPending, result.Outcome);
            Assert.True(sw.Elapsed >= TimeSpan.FromMilliseconds(250), $"Returned before grace period: {sw.Elapsed}");
            Assert.True(tracker.TryGetPending(TestProcessId, out _));
        }
        finally
        {
            release.Set();
        }

        AssertTrackerClearsWithin(tracker, TimeSpan.FromSeconds(2));
    }

    [Fact]
    public void Execute_UnknownProcessId_SkipsModalDetection()
    {
        var tracker = new PendingInvokeTracker();
        var enumeratorCalled = false;

        var result = ModalAwareInvoker.Execute(
            0,
            "Invoke on 'OK'",
            () => Thread.Sleep(100),
            tracker,
            gracePeriod: TimeSpan.FromMilliseconds(600),
            windowEnumerator: _ =>
            {
                enumeratorCalled = true;
                return new List<Win32WindowInfo>();
            }
        );

        Assert.Equal(PatternCallOutcome.Completed, result.Outcome);
        Assert.False(enumeratorCalled);
    }

    private static void AssertTrackerClearsWithin(PendingInvokeTracker tracker, TimeSpan timeout)
    {
        var sw = Stopwatch.StartNew();
        while (sw.Elapsed < timeout)
        {
            if (!tracker.TryGetPending(TestProcessId, out _))
            {
                return;
            }

            Thread.Sleep(10);
        }

        Assert.Fail("Tracker still reports a pending invoke after the action completed.");
    }
}
