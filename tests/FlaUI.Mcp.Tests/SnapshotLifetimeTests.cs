using FlaUI.Core.AutomationElements;
using FlaUI.Mcp.Core;
using Xunit;

namespace FlaUI.Mcp.Tests;

public sealed class SnapshotLifetimeTests
{
    [Fact]
    public void ProcessIdRemainsAvailableWhenNativeIdentityCannotBeCaptured()
    {
        var registry = new ElementRegistry();
        registry.SetWindowProcessId("w1", 42);
        Assert.Throws<InvalidOperationException>(() => registry.SetWindowIdentity("w1", 42, 0));
        var reference = registry.Register("w1", Element());
        Assert.Equal(42, registry.GetProcessIdForRef(reference));
    }

    // A null sentinel exercises ref allocation/invalidation without constructing a UIA provider.
    // These tests deliberately do not dereference registered elements.
    private static AutomationElement Element() => null!;

    [Fact]
    public void NewSnapshotRejectsLatePublicationAndDoesNotRecycleRefs()
    {
        var registry = new ElementRegistry();
        var first = registry.BeginSnapshot("w1");
        var oldRef = registry.Register("w1", Element(), first);
        var second = registry.BeginSnapshot("w1");
        var current = Element();
        var newRef = registry.Register("w1", current, second);

        Assert.Throws<OperationCanceledException>(() => registry.Register("w1", Element(), first));
        Assert.False(registry.HasElement(oldRef));
        Assert.NotEqual(oldRef, newRef);
        Assert.True(registry.HasElement(newRef));
    }

    [Fact]
    public void CancelledSnapshotCannotClearExistingRefs()
    {
        var registry = new ElementRegistry();
        var reference = registry.Register("w1", Element());
        var operation = new OperationContext();
        operation.Stop.Cancel();
        OperationContext.Current.Value = operation;
        try
        {
            Assert.Throws<OperationCanceledException>(() => registry.BeginSnapshot("w1"));
            Assert.True(registry.HasElement(reference));
        }
        finally
        {
            OperationContext.Current.Value = null;
        }
    }

    [Fact]
    public void CancellationDuringProviderReadDiscardsReturnedValue()
    {
        var operation = new OperationContext();
        OperationContext.Current.Value = operation;
        try
        {
            Assert.Throws<OperationCanceledException>(() =>
                SnapshotBuilder.Read(() =>
                {
                    operation.Stop.Cancel();
                    return "late provider result";
                })
            );
            var called = false;
            Assert.Throws<OperationCanceledException>(() => SnapshotBuilder.Read(() => called = true));
            Assert.False(called);
        }
        finally
        {
            OperationContext.Current.Value = null;
        }
    }

    [Fact]
    public void ConcurrentRegistrationsAllocateDistinctRefs()
    {
        var registry = new ElementRegistry();
        var generation = registry.BeginSnapshot("w1");
        var refs = new System.Collections.Concurrent.ConcurrentBag<string>();
        Parallel.For(0, 500, _ => refs.Add(registry.Register("w1", Element(), generation)));
        Assert.Equal(500, refs.Distinct().Count());
        Assert.All(refs, reference => Assert.True(registry.HasElement(reference)));
    }
}
