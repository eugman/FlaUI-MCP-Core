using FlaUI.Mcp.Core;
using Xunit;

namespace FlaUI.Mcp.Tests;

public class PendingInvokeTrackerTests
{
    [Fact]
    public void TryGetPending_NoPendingCalls_ReturnsFalse()
    {
        var tracker = new PendingInvokeTracker();
        Assert.False(tracker.TryGetPending(42, out _));
    }

    [Fact]
    public void TryGetPending_AfterBegin_ReturnsTrueForThatProcessOnly()
    {
        var tracker = new PendingInvokeTracker();
        tracker.Begin(42, "Invoke on 'OK'");

        Assert.True(tracker.TryGetPending(42, out var info));
        Assert.Equal("Invoke on 'OK'", info.Description);
        Assert.False(tracker.TryGetPending(43, out _));
    }

    [Fact]
    public void TryGetPending_AfterComplete_ReturnsFalse()
    {
        var tracker = new PendingInvokeTracker();
        var info = tracker.Begin(42, "Invoke on 'OK'");
        tracker.Complete(info);

        Assert.False(tracker.TryGetPending(42, out _));
    }

    [Fact]
    public void TryGetPending_ProcessIdZero_NeverMatches()
    {
        var tracker = new PendingInvokeTracker();
        tracker.Begin(0, "Invoke on unknown process");

        Assert.False(tracker.TryGetPending(0, out _));
    }

    [Fact]
    public void DescribeBlocked_IncludesDescriptionAndModalTitle()
    {
        var tracker = new PendingInvokeTracker();
        var info = tracker.Begin(42, "Invoke on 'Open...'");
        info.ModalTitle = "Open File";

        var message = PendingInvokeTracker.DescribeBlocked(info);

        Assert.Contains("Invoke on 'Open...'", message);
        Assert.Contains("Open File", message);
        Assert.Contains("windows_send_keys", message);
    }
}
