using System.Text.Json;
using FlaUI.Mcp.Core;

namespace FlaUI.Mcp.Tools;

public sealed class FindTool(ElementQuery query) : ToolBase
{
    internal static readonly JsonSerializerOptions SelectorOptions = new() { PropertyNameCaseInsensitive = true };

    public override string Name => "windows_find";
    public override string Description =>
        "Find controls with exact UIA selectors and optional ancestor scope; returns compact properties, patterns and fresh refs. Omit selector for bounded raw-tree inspection. An absent state field means unknown. Does not invalidate snapshot refs.";
    internal static object SelectorSchema =>
        new
        {
            type = "object",
            properties = new
            {
                automationId = new { type = "string" },
                name = new { type = "string" },
                rootOnly = new { type = "boolean", description = "Match only the search root, not descendants." },
                controlType = new { type = "string", description = "UIA type, e.g. Button, Edit, Tree, MenuItem" },
                className = new { type = "string" },
                value = new { type = "string" },
                visible = new { type = "boolean" },
                pattern = new
                {
                    type = "string",
                    @enum = new[] { "Value", "SelectionItem", "Toggle", "Invoke", "ExpandCollapse" },
                },
                text = new
                {
                    type = "string",
                    description = "Matches name OR value. Prefer it when you know what a control shows on screen: grid, tree and list rows usually carry a positional name such as 'Node0' or 'Name row 87' with the label in value.",
                },
            },
        };
    public override object InputSchema =>
        new
        {
            type = "object",
            properties = new
            {
                handle = new { type = "string" },
                selector = SelectorSchema,
                within = SelectorSchema,
                includeOwned = new
                {
                    type = "boolean",
                    description = "Also search other visible native windows of this process, including popup menus (unscoped only).",
                },
                maxDepth = new { type = "integer" },
                maxNodes = new
                {
                    type = "integer",
                    description = "Client-side traversal/result budget; cannot interrupt a blocking provider query.",
                },
                maxResults = new { type = "integer" },
                ancestry = new { type = "boolean" },
                includeBounds = new
                {
                    type = "boolean",
                    description = "Include physical screen bounds; omitted by default.",
                },
            },
            required = new[] { "handle" },
        };

    public override Task<McpToolResult> ExecuteAsync(JsonElement? arguments)
    {
        try
        {
            var a = arguments ?? throw new ArgumentException("Arguments required");
            if (
                a.ValueKind != JsonValueKind.Object
                || !a.TryGetProperty("handle", out var handle)
                || handle.ValueKind != JsonValueKind.String
                || string.IsNullOrWhiteSpace(handle.GetString())
            )
            {
                throw new ArgumentException(
                    "handle must be a window handle from windows_list_windows, not an element ref."
                );
            }

            var selector = a.TryGetProperty("selector", out var s)
                ? s.Deserialize<ElementSelector>(SelectorOptions)
                    ?? throw new ArgumentException("selector must be an object, not null.")
                : new();
            var within = a.TryGetProperty("within", out var w)
                ? w.Deserialize<ElementSelector>(SelectorOptions)
                    ?? throw new ArgumentException("within must be an object, not null.")
                : null;
            int Limit(string key, int fallback) => a.TryGetProperty(key, out var v) ? v.GetInt32() : fallback;
            var includeOwned = GetBoolArgument(arguments, "includeOwned");
            var found = query.Find(
                handle.GetString()!,
                selector,
                within,
                Limit("maxDepth", 24),
                Limit("maxNodes", 3000),
                Limit("maxResults", 30),
                includeOwned,
                includeBounds: GetBoolArgument(arguments, "includeBounds"),
                describeMisses: true
            );
            object response = GetBoolArgument(arguments, "ancestry")
                ? new
                {
                    result = found,
                    ancestors = found.Elements.Select(e => new { e.Ref, chain = query.Ancestors(e.Ref) }),
                }
                : found;
            var json = JsonSerializer.SerializeToNode(response, McpProtocol.JsonOptions)!.AsObject();
            var hints = new[]
            {
                EmptyHint(selector, found),
                PopupHint(selector, found, includeOwned),
                TruncationHint(found),
            }
                .OfType<string>()
                .ToArray();
            if (hints.Length > 0)
            {
                json["hint"] = string.Join(" ", hints);
            }

            return Task.FromResult(TextResult(json.ToJsonString(McpProtocol.JsonOptions)));
        }
        catch (ElementNotFoundException ex)
        {
            // Only windows_find: mutations continue to require a unique resolved target.
            return Task.FromResult(
                TextResult(
                    JsonSerializer.Serialize(
                        new
                        {
                            elements = Array.Empty<object>(),
                            complete = false,
                            scopeFound = false,
                            scope = "requested ancestor was not found; descendants were not searched",
                            outcome = "scope_not_found",
                            hint = ex.Message,
                        },
                        McpProtocol.JsonOptions
                    )
                )
            );
        }
        catch (ProviderBlockedException ex)
        {
            return Task.FromResult(BlockedResult(ex.Pending));
        }
        catch (Exception ex)
            when (ex is ArgumentException or JsonException or InvalidOperationException or FormatException)
        {
            return Task.FromResult(ErrorResult("windows_find: " + ex.Message));
        }
    }

    internal static string? EmptyHint(ElementSelector selector, QueryResult found)
    {
        if (
            found.Elements.Count > 0
            || (selector.Name ?? selector.ControlType ?? selector.Value ?? selector.Text) == null
        )
        {
            return null;
        }
        // Telling a caller who already used text: to try text: wastes the hint, so say what is left.
        var textAdvice =
            selector.Text == null
                ? "Displayed text is often in value rather than name, so try text: which matches either."
                : "text: already matches name and value, so the control shows something else or is not in this scope.";
        return $"No match. Selectors are exact and case-sensitive. {textAdvice} A menu entry may be a Button rather than a MenuItem; retry with fewer fields.";
    }

    // A MenuItem search of an open popup can match only submenu parents, making the menu look nearly empty.
    internal static string? PopupHint(ElementSelector selector, QueryResult found, bool includeOwned) =>
        includeOwned && selector.ControlType == "MenuItem" && found.Elements.Count > 0
            ? "Menu entries are often Buttons; only submenu parents may be MenuItems. Search the open popup without controlType to see every entry."
            : null;

    internal static string? TruncationHint(QueryResult found) =>
        found.Elements.Any(e => e.ValueTruncated)
            ? "Some values are truncated; windows_get_text returns the full value."
            : null;
}
