using System.Text.Json;
using FlaUI.Mcp;
using FlaUI.Mcp.Core;
using FlaUI.Mcp.Tools;
using Xunit;

namespace FlaUI.Mcp.Tests;

public sealed class BatchDispatchTests
{
    private static JsonElement[] Rows(string json) => JsonSerializer.Deserialize<JsonElement[]>(json)!;

    [Fact]
    public async Task DispatchesExistingToolsInOrderWithOriginalArguments()
    {
        var rows = Rows(
            """[{"action":"fill","ref":"target","value":"text"},{"action":"snapshot","handle":"window"}]"""
        );
        var calls = new List<(string Tool, string Json)>();
        var result = await BatchTool.ExecuteRows(
            rows,
            (tool, row) =>
            {
                calls.Add((tool, row.GetRawText()));
                return Task.FromResult(new McpToolResult { Content = [new() { Type = "text", Text = "done" }] });
            }
        );
        Assert.NotEqual(true, result.IsError);
        Assert.Equal(new[] { "windows_fill", "windows_snapshot" }, calls.Select(c => c.Tool));
        Assert.Equal(rows.Select(r => r.GetRawText()), calls.Select(c => c.Json));
    }

    [Theory]
    [InlineData(true, 1)]
    [InlineData(false, 2)]
    public async Task StopOnErrorControlsContinuationWithNumberedOutput(bool stopOnError, int expectedCalls)
    {
        var calls = 0;
        var result = await BatchTool.ExecuteRows(
            Rows("""[{"action":"click","ref":"first"},{"action":"click","ref":"second"}]"""),
            (_, _) =>
            {
                calls++;
                return Task.FromResult(
                    new McpToolResult { IsError = true, Content = [new() { Type = "text", Text = "failed" }] }
                );
            },
            stopOnError
        );
        Assert.NotEqual(true, result.IsError); // Upstream batch returns numbered text, including failures.
        Assert.Equal(expectedCalls, calls);
        Assert.Contains("1. click: failed", result.Content[0].Text);
        Assert.Equal(stopOnError, result.Content[0].Text!.Contains("Stopped at action"));
        Assert.Equal("failed", result.Outcome!.Dispatch);
        Assert.Equal(expectedCalls, result.Outcome.Steps!.Count);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task PendingActionStopsBeforeDependentInputRegardlessOfStopOnError(bool stopOnError)
    {
        var calls = 0;
        var result = await BatchTool.ExecuteRows(
            Rows("""[{"action":"click","ref":"open"},{"action":"type","text":"danger"}]"""),
            (_, _) =>
            {
                calls++;
                return Task.FromResult(
                    new McpToolResult
                    {
                        Outcome = new("completed", "pending", "pattern-1", "Dialog", "operation-1"),
                        Content = [new() { Type = "text", Text = "Dialog opened" }],
                    }
                );
            },
            stopOnError
        );
        Assert.Equal(1, calls);
        Assert.NotEqual(true, result.IsError);
        Assert.True(result.Outcome!.IsPending);
        Assert.Equal("pattern-1", result.Outcome.PendingPatternId);
        Assert.Equal("operation-1", result.Outcome.OperationId);
        Assert.Single(result.Outcome.Steps!);
        var json = JsonSerializer.Serialize(result);
        Assert.Contains("\"structuredContent\"", json);
        Assert.Contains("\"steps\"", json);
    }

    [Fact]
    public async Task OrdinaryTextIsNotMistakenForPendingControlState()
    {
        var calls = 0;
        var result = await BatchTool.ExecuteRows(
            Rows("""[{"action":"click","ref":"first"},{"action":"click","ref":"second"}]"""),
            (_, _) =>
            {
                calls++;
                return Task.FromResult(
                    new McpToolResult
                    {
                        Content = [new() { Type = "text", Text = "Selected modal settings; label says still running" }],
                    }
                );
            }
        );
        Assert.NotEqual(true, result.IsError);
        Assert.Equal(2, calls);
    }

    [Theory]
    [InlineData("failed", "pending", true)]
    [InlineData("timed_out_pending", "unknown", true)]
    public async Task ExistingPendingOrTimeoutNeverDispatchesNextStep(string dispatch, string provider, bool error)
    {
        var calls = 0;
        var result = await BatchTool.ExecuteRows(
            Rows("""[{"action":"snapshot","handle":"window"},{"action":"type","text":"unsafe"}]"""),
            (_, _) =>
            {
                calls++;
                return Task.FromResult(
                    new McpToolResult
                    {
                        IsError = error,
                        Outcome = new(dispatch, provider),
                        Content = [],
                    }
                );
            },
            stopOnError: false
        );
        Assert.Equal(1, calls);
        Assert.True(result.Outcome!.IsPending);
    }

    [Fact]
    public async Task RefOnlySnapshotFailsInsteadOfCapturingFocusedWindow()
    {
        var result = await BatchTool.ExecuteRows(
            Rows("""[{"action":"snapshot","ref":"wrong"}]"""),
            (_, _) => throw new Exception("Must not dispatch ambiguous snapshot")
        );
        Assert.Contains("Snapshot does not support ref", result.Content[0].Text);
    }

    [Fact]
    public async Task UntargetedSnapshotAndTypeRetainForegroundDefaults()
    {
        var calls = new List<string>();
        await BatchTool.ExecuteRows(
            Rows("""[{"action":"snapshot"},{"action":"type","text":"hello"}]"""),
            (tool, _) =>
            {
                calls.Add(tool);
                return Task.FromResult(new McpToolResult { Content = [] });
            }
        );
        Assert.Equal(new[] { "windows_snapshot", "windows_type" }, calls);
    }
}
