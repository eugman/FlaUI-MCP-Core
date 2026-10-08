using System.Diagnostics;
using System.Text.Json;
using FlaUI.Mcp.Core;

namespace FlaUI.Mcp.Tools;

/// <summary>Read-only polling; incomplete observations never establish absence.</summary>
public sealed class WaitTool(ElementQuery query) : ToolBase
{
    public override string Name => "windows_wait";
    public override string Description =>
        "Wait for scoped UIA presence, settled absence, or one control's value/selected/toggle state. Does not send input. Incomplete searches cannot prove absence; rootOnly excludes descendants.";
    public override object InputSchema =>
        new
        {
            type = "object",
            properties = new
            {
                handle = new { type = "string" },
                selector = FindTool.SelectorSchema,
                within = FindTool.SelectorSchema,
                includeOwned = new { type = "boolean" },
                state = new { type = "string", @enum = new[] { "present", "absent", "value", "selected", "toggle" } },
                expected = new
                {
                    description = "String for value/toggle (On, Off, Indeterminate), boolean for selected.",
                },
                timeoutMs = new
                {
                    type = "integer",
                    minimum = 1,
                    maximum = 25000,
                    @default = 5000,
                },
                settleMs = new
                {
                    type = "integer",
                    minimum = 0,
                    maximum = 25000,
                    @default = 500,
                },
                maxNodes = new
                {
                    type = "integer",
                    minimum = 1,
                    maximum = 20000,
                    @default = 3000,
                },
            },
            required = new[] { "handle", "selector", "state" },
        };

    public override async Task<McpToolResult> ExecuteAsync(JsonElement? arguments)
    {
        try
        {
            var a = arguments ?? throw new ArgumentException("Arguments required.");
            var handle = GetStringArgument(a, "handle");
            if (string.IsNullOrWhiteSpace(handle))
            {
                throw new ArgumentException("A window handle is required, not an element ref.");
            }

            var selector =
                a.GetProperty("selector").Deserialize<ElementSelector>(FindTool.SelectorOptions)
                ?? throw new ArgumentException("selector must be an object.");
            var within = a.TryGetProperty("within", out var w)
                ? w.Deserialize<ElementSelector>(FindTool.SelectorOptions)
                    ?? throw new ArgumentException("within must be an object.")
                : null;
            ElementQuery.ValidateSelector(selector);
            if (within != null)
            {
                ElementQuery.ValidateSelector(within);
            }

            var state = GetStringArgument(a, "state") ?? "";
            var expected = a.TryGetProperty("expected", out var e) ? e : default;
            ValidateExpectation(state, expected);
            int Number(string key, int fallback) => a.TryGetProperty(key, out var n) ? n.GetInt32() : fallback;
            var timeout = Number("timeoutMs", 5000);
            var settle = Number("settleMs", 500);
            var maxNodes = Number("maxNodes", 3000);
            var includeOwned = GetBoolArgument(a, "includeOwned");
            if (timeout is < 1 or > 25000 || settle < 0 || settle >= timeout || state == "absent" && settle == 0)
            {
                throw new ArgumentException(
                    "timeoutMs must be 1..25000; settleMs must be below timeoutMs and positive for absence."
                );
            }

            var watch = Stopwatch.StartNew();
            TimeSpan? since = null;
            QueryResult? last = null;
            while (watch.ElapsedMilliseconds < timeout)
            {
                OperationContext.Check();
                // Polls don't register refs; only the observation that settles gets them.
                last = query.Find(
                    handle,
                    selector,
                    within,
                    maxNodes: maxNodes,
                    maxResults: 100,
                    includeOwned: includeOwned,
                    budget: new SearchBudget(maxNodes, TimeSpan.FromMilliseconds(timeout) - watch.Elapsed),
                    register: false
                );
                if (watch.ElapsedMilliseconds >= timeout)
                {
                    break;
                }

                if (Satisfied(last, state, expected))
                {
                    since ??= watch.Elapsed;
                }
                else
                {
                    since = null;
                }

                if (since != null && (watch.Elapsed - since.Value).TotalMilliseconds >= settle)
                {
                    var observed = query.WithRefs(handle, last);
                    return TextResult(
                        JsonSerializer.Serialize(
                            new
                            {
                                state,
                                settledMs = settle,
                                observed.Complete,
                                observed.Elements,
                            },
                            McpProtocol.JsonOptions
                        )
                    );
                }

                await Task.Delay(
                    Math.Min(100, Math.Max(1, timeout - (int)watch.ElapsedMilliseconds)),
                    OperationContext.Current.Value?.Stop.Token ?? CancellationToken.None
                );
            }

            return ErrorResult(
                $"Condition not verified within {timeout}ms. Last search: complete={last?.Complete}, matches={last?.Elements.Count}, scope={last?.Scope}. No input sent."
            );
        }
        catch (ProviderBlockedException ex)
        {
            return BlockedResult(ex.Pending);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return ErrorResult("windows_wait: " + ex.Message + " No input sent.");
        }
    }

    internal static void ValidateExpectation(string state, JsonElement expected)
    {
        if (state is not ("present" or "absent" or "value" or "selected" or "toggle"))
        {
            throw new ArgumentException("Unknown wait state.");
        }

        if (state is "value" or "toggle" && expected.ValueKind != JsonValueKind.String)
        {
            throw new ArgumentException("expected must be a string.");
        }

        if (state == "selected" && expected.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
        {
            throw new ArgumentException("expected must be boolean.");
        }

        if (state == "toggle" && expected.GetString() is not ("On" or "Off" or "Indeterminate"))
        {
            throw new ArgumentException("expected toggle must be On, Off, or Indeterminate.");
        }
    }

    internal static bool Satisfied(QueryResult result, string state, JsonElement expected)
    {
        if (state == "present")
        {
            return result.Elements.Count > 0;
        }

        if (!result.Complete)
        {
            return false;
        }

        if (state == "absent")
        {
            return result.Elements.Count == 0;
        }

        if (result.Elements.Count != 1)
        {
            return false;
        }

        var item = result.Elements[0];
        return state switch
        {
            "value" => !item.ValueTruncated && item.Value != null && item.Value == expected.GetString(),
            "selected" => item.Selected != null && item.Selected == expected.GetBoolean(),
            "toggle" => item.ToggleState != null && item.ToggleState == expected.GetString(),
            _ => false,
        };
    }
}
