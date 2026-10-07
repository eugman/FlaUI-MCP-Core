using FlaUI.Mcp.Core;
using Xunit;

namespace FlaUI.Mcp.Tests;

public sealed class PendingIdentityTests
{
    [Theory]
    [InlineData(200L)]
    [InlineData(null)]
    public void ReusedOrExitedProcessDoesNotInheritPendingCall(long? replacement)
    {
        long? started = 100;
        var tracker = new PendingInvokeTracker(_ => started);
        var old = tracker.Begin(42, "old call");
        Assert.True(tracker.TryGetPending(42, out _));
        started = replacement;
        Assert.False(tracker.TryGetPending(42, out _));
        if (replacement != null)
        {
            var current = tracker.Begin(42, "new call");
            tracker.Complete(old);
            Assert.True(tracker.TryGetPending(42, out var found));
            Assert.Same(current, found);
        }
    }

    [Fact]
    public void UnreadableProcessIdentityIsNotTreatedAsExit()
    {
        long? started = 100;
        var tracker = new PendingInvokeTracker(_ => started);
        var call = tracker.Begin(42, "call");
        started = 0;
        Assert.True(tracker.TryGetPending(42, out var found));
        Assert.Same(call, found);
        Assert.False(tracker.TryGetPending(0, out _));
    }
}
