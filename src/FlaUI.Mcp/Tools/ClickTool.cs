using System.Text.Json;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Definitions;
using FlaUI.Core.Input;
using FlaUI.Mcp.Core;

namespace FlaUI.Mcp.Tools;

/// <summary>
/// Click an element by ref
/// </summary>
public class ClickTool : ToolBase
{
    private readonly ElementRegistry _elementRegistry;
    private readonly PendingInvokeTracker _invokeTracker;

    public ClickTool(ElementRegistry elementRegistry, PendingInvokeTracker? invokeTracker = null)
    {
        _elementRegistry = elementRegistry;
        _invokeTracker = invokeTracker ?? new PendingInvokeTracker();
    }

    public override string Name => "windows_click";

    public override string Description =>
        "Click an element by its ref (from windows_snapshot). Prefers Invoke pattern for reliability, "
        + "falls back to mouse click if needed. Menu items and controls whose name ends in \"...\" get a physical click "
        + "by default, because an Invoke that opens a menu or dialog blocks UI Automation until it closes; pass physical=false to force Invoke. "
        + "If the click opens a modal dialog, returns immediately with the dialog title instead of waiting for the dialog to close. "
        + "A right-click after selecting a tree or list row can start inline rename; open context menus with windows_send_keys Shift+F10 or Apps.";

    public override object InputSchema =>
        new
        {
            type = "object",
            properties = new
            {
                handle = new
                {
                    type = "string",
                    description = "Optional window handle; when supplied, the ref must belong to this window.",
                },
                physical = new
                {
                    type = "boolean",
                    description = "Use a physical mouse click with foreground and native-window hit checks. Defaults to true for menu items and names ending in \"...\", otherwise false. These checks do not prove which UIA child receives the click; a container's clickable point may hit a child control.",
                },
                @ref = new { type = "string", description = "Element ref from windows_snapshot (e.g., 'w1e5')" },
                button = new
                {
                    type = "string",
                    @enum = new[] { "left", "right", "middle" },
                    description = "Mouse button to click (default: left)",
                },
                doubleClick = new { type = "boolean", description = "Whether to double-click (default: false)" },
            },
            required = new[] { "ref" },
        };

    public override Task<McpToolResult> ExecuteAsync(JsonElement? arguments)
    {
        var refId = GetStringArgument(arguments, "ref");
        if (string.IsNullOrEmpty(refId))
        {
            return Task.FromResult(ErrorResult("Missing required argument: ref"));
        }

        var button = GetStringArgument(arguments, "button") ?? "left";
        var doubleClick = GetBoolArgument(arguments, "doubleClick", false);
        bool? requestedPhysical =
            arguments is { ValueKind: JsonValueKind.Object } a && a.TryGetProperty("physical", out _)
                ? GetBoolArgument(arguments, "physical", false)
                : null;

        // Fail fast if this app's UIA provider is already blocked by an earlier call
        var processId = _elementRegistry.GetProcessIdForRef(refId);
        if (_invokeTracker.TryGetPending(processId, out var pending))
        {
            return Task.FromResult(BlockedResult(pending));
        }

        try
        {
            OperationContext.Check();
            var handle = GetStringArgument(arguments, "handle");
            var element = _elementRegistry.ResolveRef(refId, handle);
            void Validate() => _elementRegistry.ValidateReference(refId, handle);
            OperationContext.Check();
            var elementName = element.Properties.Name.ValueOrDefault ?? refId;
            var physical =
                requestedPhysical ?? PrefersPhysical(element.Properties.ControlType.ValueOrDefault, elementName);

            // Try Invoke pattern first (most reliable for buttons)
            if (!physical && button == "left" && !doubleClick && element.Patterns.Invoke.IsSupported)
            {
                // Pattern mutations share the input lease so they cannot interleave with physical input.
                using var lease = GuardedInput.AcquireLease();
                var invokePattern = element.Patterns.Invoke.Pattern;
                var result = ModalAwareInvoker.Execute(
                    processId,
                    $"Invoke on '{elementName}'",
                    () => MutationGuard.Execute(Validate, () => invokePattern.Invoke()),
                    _invokeTracker
                );
                return Task.FromResult(PatternResult(result, $"Invoked {elementName}"));
            }

            // Try Toggle pattern for checkboxes
            if (!physical && button == "left" && !doubleClick && element.Patterns.Toggle.IsSupported)
            {
                using var lease = GuardedInput.AcquireLease();
                var togglePattern = element.Patterns.Toggle.Pattern;
                var result = ModalAwareInvoker.Execute(
                    processId,
                    $"Toggle on '{elementName}'",
                    () => MutationGuard.Execute(Validate, () => togglePattern.Toggle()),
                    _invokeTracker
                );
                // Providers may update asynchronously or return a cached pre-toggle value.
                return Task.FromResult(
                    PatternResult(
                        result,
                        $"Sent toggle request to {elementName}; inspect fresh state to verify the result"
                    )
                );
            }

            // Try SelectionItem pattern for list items
            if (!physical && button == "left" && !doubleClick && element.Patterns.SelectionItem.IsSupported)
            {
                using var lease = GuardedInput.AcquireLease();
                var selectionPattern = element.Patterns.SelectionItem.Pattern;
                var result = ModalAwareInvoker.Execute(
                    processId,
                    $"Select on '{elementName}'",
                    () => MutationGuard.Execute(Validate, () => selectionPattern.Select()),
                    _invokeTracker
                );
                return Task.FromResult(PatternResult(result, $"Selected {elementName}"));
            }

            // Fall back to mouse click
            using var input = new GuardedInput(_elementRegistry.InputForRef(refId));

            var mouseButton = button switch
            {
                "right" => MouseButton.Right,
                "middle" => MouseButton.Middle,
                _ => MouseButton.Left,
            };

            if (doubleClick)
            {
                input.Click(element, mouseButton, true);
                return Task.FromResult(TextResult($"Double-clicked {elementName}"));
            }
            else
            {
                var opensDialog = physical && EndsWithEllipsis(elementName) && processId != 0;
                var before = opensDialog ? Win32Desktop.GetTopLevelWindows(processId) : [];
                input.Click(element, mouseButton);
                if (!opensDialog)
                {
                    return Task.FromResult(TextResult($"Clicked {elementName}"));
                }
                // A physical click returns before its dialog exists; report the window so agents don't list too early.
                for (var waited = 0; waited < 2000; waited += 100)
                {
                    if (NewWindowTitle(before, Win32Desktop.GetTopLevelWindows(processId)) is { } title)
                    {
                        return Task.FromResult(
                            TextResult(
                                $"Clicked {elementName}; window \"{title}\" opened. Get its handle from windows_list_windows."
                            )
                        );
                    }

                    Thread.Sleep(100);
                }

                return Task.FromResult(TextResult($"Clicked {elementName}; no new window appeared within 2 seconds."));
            }
        }
        catch (Exception ex)
        {
            IReadOnlyList<Win32WindowInfo> windows = [];
            try
            {
                var owner = _elementRegistry.GetProcessIdForRef(refId);
                if (owner != 0)
                {
                    windows = Win32Desktop.GetTopLevelWindows(owner);
                }
            }
            catch { }

            return Task.FromResult(
                ErrorResult($"Failed to click {refId}: {ClickFailure(ex.Message, ex.GetType().Name, windows)}")
            );
        }
    }

    // An empty reason invites a blind retry; name the exception and any dialog that seems to block input.
    internal static string ClickFailure(string message, string exceptionType, IReadOnlyList<Win32WindowInfo> windows)
    {
        var reason = string.IsNullOrWhiteSpace(message) ? exceptionType : message;
        var modal = windows.Any(w => !w.IsEnabled && !w.IsToolWindow)
            ? windows.FirstOrDefault(w => w.IsEnabled && !w.IsToolWindow && !w.IsCloaked && w.Title.Length > 0)
            : null;
        return modal == null
            ? reason
            : $"{reason.TrimEnd('.')}. Window \"{modal.Title}\" appears to be a modal dialog blocking input; handle it first (see windows_list_windows).";
    }

    /// <summary>
    /// Menus and "..." commands usually open a popup or modal. Invoke on them does not return
    /// until it closes, which blocks the provider the agent needs to inspect that popup.
    /// </summary>
    internal static bool PrefersPhysical(ControlType type, string? name) =>
        type == ControlType.MenuItem || EndsWithEllipsis(name);

    private static bool EndsWithEllipsis(string? name)
    {
        var trimmed = name?.TrimEnd() ?? "";
        return trimmed.EndsWith("...") || trimmed.EndsWith('…');
    }

    internal static string? NewWindowTitle(IEnumerable<Win32WindowInfo> before, IEnumerable<Win32WindowInfo> after)
    {
        var known = before.Select(w => w.Hwnd).ToHashSet();
        return after.FirstOrDefault(w => !known.Contains(w.Hwnd) && w.Title.Length > 0)?.Title;
    }

    /// <summary>
    /// Map a modal-aware pattern call result to a tool result.
    /// </summary>
    private static McpToolResult PatternResult(PatternCallResult result, string completedMessage)
    {
        var response = result.Outcome switch
        {
            PatternCallOutcome.Completed => TextResult(completedMessage),
            PatternCallOutcome.ModalDetected => TextResult(
                $"{completedMessage}; a modal dialog \"{result.ModalTitle}\" opened and blocks UI Automation on this app until it closes. "
                    + "Find it with windows_list_windows, then use windows_screenshot or windows_send_keys (no ref) with its handle."
            ),
            _ => TextResult(
                $"{completedMessage}; the app's handler is still running. Check windows_operation_status or take a screenshot "
                    + "by handle before continuing; do not repeat the click."
            ),
        };
        return response with { Outcome = ToolOutcome.FromPattern(result) };
    }
}
