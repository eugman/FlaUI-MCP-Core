using System.Text.Json;
using FlaUI.Mcp;
using FlaUI.Mcp.Core;
using FlaUI.Mcp.Tools;
using Xunit;

namespace FlaUI.Mcp.Tests;

public sealed class ToolOutcomeTests
{
    [Fact]
    public void OptionalStructuredContentPreservesLegacyTextShape()
    {
        var plain = new McpToolResult { Content = [new() { Type = "text", Text = "done" }] };
        Assert.False(JsonSerializer.SerializeToElement(plain).TryGetProperty("structuredContent", out _));
        var pending = plain with
        {
            Outcome = ToolOutcome.FromPattern(new(PatternCallOutcome.ModalDetected, "Dialog", "pattern")),
        };
        var structured = JsonSerializer.SerializeToElement(pending).GetProperty("structuredContent");
        Assert.Equal("completed", structured.GetProperty("dispatch").GetString());
        Assert.Equal("pending", structured.GetProperty("provider").GetString());
        Assert.Equal("pattern", structured.GetProperty("pendingPatternId").GetString());
        Assert.False(structured.TryGetProperty("IsPending", out _));
        Assert.True(pending.Outcome!.IsPending);
        Assert.False(ToolOutcome.FromPattern(new(PatternCallOutcome.Completed)).IsPending);
    }

    [Fact]
    public async Task CompletedDispatchLinksPendingProviderWithoutBlockingExplicitRecoveryDispatch()
    {
        using var release = new ManualResetEventSlim();
        var tracker = new PendingInvokeTracker(_ => 456);
        var registry = new ToolRegistry { ResolveTarget = _ => new ProcessIdentity(123, 456) };
        registry.RegisterTool(new PendingTool(tracker, release));
        registry.RegisterTool(new RecoveryProbe());
        try
        {
            var result = await registry.ExecuteToolAsync("pending-test", null);
            Assert.True(result.Outcome!.IsPending);
            Assert.NotNull(result.Outcome.PendingPatternId);
            var pattern = JsonSerializer.SerializeToElement(tracker.Status())[0];
            var parent = pattern.GetProperty("parentOperationId").GetString()!;
            Assert.Equal(parent, result.Outcome.OperationId);
            Assert.Equal(result.Outcome.PendingPatternId, pattern.GetProperty("operationId").GetString());
            Assert.Equal(456, pattern.GetProperty("ProcessStartedTicks").GetInt64());
            var status = JsonSerializer.SerializeToElement(registry.Operations.Status(parent, tracker))[0];
            Assert.Equal("completed", status.GetProperty("dispatch").GetString());
            Assert.Equal("provider_pending", status.GetProperty("status").GetString());
            Assert.Equal("pending", status.GetProperty("provider").GetString());
            Assert.Equal(result.Outcome.PendingPatternId, status.GetProperty("pendingPatternIds")[0].GetString());
            // Provider guards still decide which real operations are safe. The coordinator
            // must not quarantine completed dispatch and deadlock explicit dialog recovery.
            Assert.NotEqual(true, (await registry.ExecuteToolAsync("recovery-probe", null)).IsError);
            release.Set();
            Assert.True(SpinWait.SpinUntil(() => !tracker.TryGetPending(123, out _), TimeSpan.FromSeconds(3)));
            status = JsonSerializer.SerializeToElement(registry.Operations.Status(parent, tracker))[0];
            Assert.Equal("idle", status.GetProperty("provider").GetString());
            Assert.Equal("completed", status.GetProperty("status").GetString());
            Assert.Empty(status.GetProperty("pendingPatternIds").EnumerateArray());
        }
        finally
        {
            release.Set();
        }
    }

    [Fact]
    public async Task StructuredBatchFailureMarksRegistryOperationAsFailedWithoutChangingBatchContract()
    {
        var registry = new ToolRegistry();
        registry.RegisterTool(new StructuredBatchFailureTool());

        var result = await registry.ExecuteToolAsync("structured-batch-failure", null);

        Assert.NotEqual(true, result.IsError);
        Assert.Equal("failed", result.Outcome!.Dispatch);
        Assert.Contains("1. click: failed", result.Content[0].Text);
        var status = JsonSerializer.SerializeToElement(registry.Operations.Status())[0];
        Assert.Equal("failed", status.GetProperty("dispatch").GetString());
        Assert.Contains("1. click: failed", status.GetProperty("Error").GetString());
    }

    private sealed class PendingTool(PendingInvokeTracker tracker, ManualResetEventSlim release) : ToolBase
    {
        public override string Name => "pending-test";
        public override string Description => "Fake provider; no UI Automation or window calls";
        public override object InputSchema => new { };

        public override Task<McpToolResult> ExecuteAsync(JsonElement? arguments)
        {
            var outcome = ModalAwareInvoker.Execute(
                123,
                "fake pending provider",
                () => release.Wait(),
                tracker,
                TimeSpan.Zero,
                _ => Array.Empty<Win32WindowInfo>()
            );
            return Task.FromResult(TextResult("arbitrary text") with { Outcome = ToolOutcome.FromPattern(outcome) });
        }
    }

    private sealed class RecoveryProbe : ToolBase
    {
        public override string Name => "recovery-probe";
        public override string Description => "No input or UIA calls";
        public override object InputSchema => new { };

        public override Task<McpToolResult> ExecuteAsync(JsonElement? arguments) =>
            Task.FromResult(TextResult("accepted"));
    }

    private sealed class StructuredBatchFailureTool : ToolBase
    {
        public override string Name => "structured-batch-failure";
        public override string Description =>
            "Produces the legacy non-error batch result with a failed structured dispatch.";
        public override object InputSchema => new { };

        public override Task<McpToolResult> ExecuteAsync(JsonElement? arguments) =>
            BatchTool.ExecuteRows(
                JsonSerializer.Deserialize<JsonElement[]>("""[{"action":"click","ref":"first"}]""")!,
                (_, _) =>
                    Task.FromResult(
                        new McpToolResult { IsError = true, Content = [new() { Type = "text", Text = "failed" }] }
                    )
            );
    }
}
