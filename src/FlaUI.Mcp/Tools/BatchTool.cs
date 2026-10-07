using System.Text.Json;
using FlaUI.Mcp.Core;

namespace FlaUI.Mcp.Tools;

public sealed class BatchTool : ToolBase
{
    // Steps dispatch through the same registry, so each keeps its tool's guards.
    private readonly ToolRegistry registry;

    internal BatchTool(ToolRegistry registry) => this.registry = registry;

    public override string Name => "windows_batch";
    public override string Description =>
        "Execute click/type/fill/wait/snapshot actions in order, returning numbered results and structured step outcomes. Stops on errors by default; always stops when an action is still pending. Never replay a pending action.";
    public override object InputSchema =>
        new
        {
            type = "object",
            properties = new
            {
                actions = new
                {
                    type = "array",
                    items = new
                    {
                        type = "object",
                        properties = new
                        {
                            action = new
                            {
                                type = "string",
                                @enum = new[] { "click", "type", "fill", "wait", "snapshot" },
                            },
                            @ref = new { type = "string" },
                            text = new { type = "string" },
                            value = new { type = "string" },
                            ms = new
                            {
                                type = "integer",
                                minimum = 0,
                                description = "Milliseconds to wait (default: 100)",
                            },
                            handle = new { type = "string", description = "Window handle for snapshot or input" },
                        },
                        required = new[] { "action" },
                    },
                },
                stopOnError = new
                {
                    type = "boolean",
                    description = "Stop executing if an action fails (default: true)",
                },
            },
            required = new[] { "actions" },
        };

    public override async Task<McpToolResult> ExecuteAsync(JsonElement? arguments)
    {
        if (arguments is not { } args || !args.TryGetProperty("actions", out var actions))
        {
            return ErrorResult("Missing actions");
        }

        var rows = actions.EnumerateArray().ToArray();
        if (rows.Length > 100)
        {
            return ErrorResult("At most 100 steps per batch");
        }

        return await ExecuteRows(
            rows,
            (tool, row) => registry.ExecuteToolAsync(tool, row),
            !args.TryGetProperty("stopOnError", out var stop) || stop.GetBoolean()
        );
    }

    internal static async Task<McpToolResult> ExecuteRows(
        JsonElement[] rows,
        Func<string, JsonElement, Task<McpToolResult>> invoke,
        bool stopOnError = true
    )
    {
        var trace = new List<string>();
        var steps = new List<ToolStepOutcome>();
        ToolOutcome? pending = null;
        foreach (var (row, index) in rows.Select((row, index) => (row, index)))
        {
            OperationContext.Check();
            try
            {
                var op = row.GetProperty("action").GetString();
                if (op == "wait")
                {
                    var ms = row.TryGetProperty("ms", out var delay) ? delay.GetInt32() : 100;
                    if (ms < 0)
                    {
                        throw new ArgumentException("Wait must be nonnegative");
                    }

                    await Task.Delay(ms, OperationContext.Current.Value?.Stop.Token ?? CancellationToken.None);
                    trace.Add($"{index + 1}. wait: Waited {ms}ms");
                    steps.Add(new(index + 1, op, false, new("completed", "returned")));
                    continue;
                }

                if (op == "snapshot" && row.TryGetProperty("ref", out _))
                {
                    throw new ArgumentException(
                        "Snapshot does not support ref; use handle or omit both for the focused window"
                    );
                }

                var tool = op switch
                {
                    "click" => "windows_click",
                    "fill" => "windows_fill",
                    "type" => "windows_type",
                    "snapshot" => "windows_snapshot",
                    _ => throw new ArgumentException($"Unknown action: {op}"),
                };
                var result = await invoke(tool, row);
                trace.Add($"{index + 1}. {op}: {string.Join("\n", result.Content.Select(c => c.Text ?? ""))}");
                steps.Add(new(index + 1, op, result.IsError == true, result.Outcome));
                if (result.Outcome?.IsPending == true)
                {
                    pending = result.Outcome;
                    trace.Add(
                        $"Stopped at action {index + 1}: operation still pending; inspect state before continuing, do not replay."
                    );
                    break;
                }

                if (result.IsError == true && stopOnError)
                {
                    trace.Add($"Stopped at action {index + 1} due to error");
                    break;
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                trace.Add($"{index + 1}. ERROR: {ex.Message}");
                steps.Add(new(index + 1, null, true, new("failed", "unknown")));
                if (!stopOnError)
                {
                    continue;
                }

                trace.Add($"Stopped at action {index + 1} due to error");
                break;
            }
        }
        // Preserve the legacy numbered text and IsError contract. Structured
        // outcomes expose failures/pending state without interpreting UI text.
        return TextResult(string.Join("\n", trace)) with
        {
            Outcome =
                pending != null
                    ? pending with
                    {
                        Steps = steps,
                    }
                    : new(steps.Any(s => s.IsError) ? "failed" : "completed", "returned", Steps: steps),
        };
    }
}
