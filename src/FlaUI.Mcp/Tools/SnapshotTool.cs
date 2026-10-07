using System.Text.Json;
using FlaUI.Core.AutomationElements;
using FlaUI.Mcp.Core;

namespace FlaUI.Mcp.Tools;

/// <summary>
/// Inspect unfamiliar accessibility structure with fresh element refs.
/// </summary>
public class SnapshotTool : ToolBase
{
    private readonly SessionManager _sessionManager;
    private readonly SnapshotBuilder _snapshotBuilder;
    private readonly PendingInvokeTracker _invokeTracker;

    public SnapshotTool(
        SessionManager sessionManager,
        ElementRegistry elementRegistry,
        PendingInvokeTracker? invokeTracker = null
    )
    {
        _sessionManager = sessionManager;
        _snapshotBuilder = new SnapshotBuilder(elementRegistry);
        _invokeTracker = invokeTracker ?? new PendingInvokeTracker();
    }

    public override string Name => "windows_snapshot";

    public override string Description =>
        "Capture accessibility snapshot of a window. Returns a structured tree with element refs "
        + "that can be used with windows_click, windows_type, etc. Prefer windows_find for known "
        + "selectors and compact state; use this to explore unfamiliar structure. A new snapshot invalidates earlier refs for that window.";

    public override object InputSchema =>
        new
        {
            type = "object",
            properties = new
            {
                handle = new
                {
                    type = "string",
                    description = "Window handle from windows_launch or windows_list_windows. If omitted, uses the foreground window.",
                },
                maxNodes = new
                {
                    type = "integer",
                    minimum = 1,
                    maximum = 100000,
                    description = "Client-side node budget (default 3000); cannot interrupt a blocking provider call.",
                },
                maxCharacters = new
                {
                    type = "integer",
                    minimum = 256,
                    maximum = 1000000,
                    description = "Output character budget including partial-result notice (default 20000).",
                },
            },
        };

    public override Task<McpToolResult> ExecuteAsync(JsonElement? arguments)
    {
        if (arguments is { } args && args.TryGetProperty("ref", out _))
        {
            return Task.FromResult(
                ErrorResult("Snapshot does not support ref; use handle or omit both for the foreground window")
            );
        }

        var handle = GetStringArgument(arguments, "handle");

        try
        {
            Window? window;
            if (!string.IsNullOrEmpty(handle))
            {
                // Walking a blocked provider's tree would hang until the global timeout.
                if (_invokeTracker.TryGetPending(_sessionManager.GetWindowProcessId(handle), out var pending))
                {
                    return Task.FromResult(BlockedResult(pending));
                }

                window = _sessionManager.GetWindow(handle);
                if (window == null)
                {
                    return Task.FromResult(ErrorResult($"Window not found: {handle}"));
                }
            }
            else
            {
                // Resolve the foreground window through Win32; a focused-element walk can hang on unrelated apps.
                var foreground = Win32Desktop.GetForegroundWindow();
                window = foreground == 0 ? null : _sessionManager.Automation.FromHandle(foreground)?.AsWindow();
                if (window == null)
                {
                    return Task.FromResult(
                        ErrorResult(
                            "No window specified and no foreground window found. Use windows_list_windows to see available windows."
                        )
                    );
                }

                // Registration applies the app allowlist.
                handle = _sessionManager.RegisterWindow(window);
            }

            var snapshot = _snapshotBuilder.BuildSnapshot(
                handle,
                window,
                GetArgument<int?>(arguments, "maxNodes") ?? 3000,
                GetArgument<int?>(arguments, "maxCharacters") ?? 20000
            );
            return Task.FromResult(TextResult(snapshot));
        }
        catch (Exception ex)
        {
            return Task.FromResult(ErrorResult($"Failed to capture snapshot: {ex.Message}"));
        }
    }
}
