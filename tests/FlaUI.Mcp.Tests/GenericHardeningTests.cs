using System.Runtime.CompilerServices;
using System.Text.Json;
using FlaUI.Core.AutomationElements;
using FlaUI.Mcp;
using FlaUI.Mcp.Core;
using Xunit;

namespace FlaUI.Mcp.Tests;

public sealed class GenericHardeningTests
{
    // No UI Automation provider is touched: the registry only stores element references.
    private static AutomationElement Element() =>
        (AutomationElement)RuntimeHelpers.GetUninitializedObject(typeof(AutomationElement));

    [Fact]
    public void RefsResolveOnlyForTheirOwningWindow()
    {
        var refs = new ElementRegistry();
        var element = Element();
        var reference = refs.Register("w1", element);

        Assert.Same(element, refs.ResolveRef(reference, "w1"));
        Assert.Contains(
            "belongs to window w1, not w2",
            Assert.Throws<ArgumentException>(() => refs.ResolveRef(reference, "w2")).Message
        );
        Assert.Contains(
            "Element not found: w1e99",
            Assert.Throws<ArgumentException>(() => refs.ResolveRef("w1e99")).Message
        );
    }

    [Fact]
    public void RepeatedObservationsCannotGrowTheRefTableWithoutBound()
    {
        var refs = new ElementRegistry();
        var element = Element();
        var last = "";
        for (var i = 0; i < ElementRegistry.MaxRefsPerWindow + 10; i++)
        {
            last = refs.Register("w1", element);
        }

        Assert.False(refs.HasElement("w1e10"));
        Assert.True(refs.HasElement("w1e11"));
        Assert.True(refs.HasElement(last));
    }

    [Fact]
    public async Task TimedOutCallQuarantinesItsProcessUntilItReturns()
    {
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var tracker = new PendingInvokeTracker(_ => 456);
        var registry = new ToolRegistry(TimeSpan.FromMilliseconds(200))
        {
            ResolveTarget = _ => new ProcessIdentity(123, 456),
            Pending = tracker,
        };
        registry.RegisterTool(new AwaitingTool(release.Task));
        try
        {
            var result = await registry.ExecuteToolAsync("awaiting", null);
            Assert.Equal("timed_out_pending", result.Outcome!.Dispatch);
            Assert.True(tracker.TryGetPending(123, out var stranded));
            Assert.Equal("timed-out awaiting", stranded.Description);
        }
        finally
        {
            release.TrySetResult();
        }

        Assert.True(SpinWait.SpinUntil(() => !tracker.TryGetPending(123, out _), TimeSpan.FromSeconds(3)));
    }

    [Fact]
    public async Task TimeoutWithoutAKnownProcessQuarantinesNothing()
    {
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var tracker = new PendingInvokeTracker(_ => 456);
        var registry = new ToolRegistry(TimeSpan.FromMilliseconds(200)) { Pending = tracker };
        registry.RegisterTool(new AwaitingTool(release.Task));
        try
        {
            await registry.ExecuteToolAsync("awaiting", null);
            Assert.Empty((System.Collections.IEnumerable)tracker.Status());
        }
        finally
        {
            release.TrySetResult();
        }
    }

    private sealed class AwaitingTool(Task release) : ToolBase
    {
        public override string Name => "awaiting";
        public override string Description => "Waits until the test releases it; no desktop access";
        public override object InputSchema => new { };

        public override async Task<McpToolResult> ExecuteAsync(JsonElement? arguments)
        {
            await release;
            return TextResult("late");
        }
    }
}
