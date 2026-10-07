using System.Text.Json;
using FlaUI.Mcp.Core;

namespace FlaUI.Mcp.Tools;

/// <summary>
/// List all open windows
/// </summary>
public class ListWindowsTool : ToolBase
{
    private readonly SessionManager _sessionManager;
    private readonly ProcessPolicy _policy;

    public ListWindowsTool(SessionManager sessionManager, ProcessPolicy? policy = null)
    {
        _sessionManager = sessionManager;
        _policy = policy ?? ProcessPolicy.AllowAll;
    }

    // Under an allowlist an empty list usually means the allowed app isn't running, not a broken server.
    internal static string EmptyMessage(ProcessPolicy policy) =>
        policy.IsRestricted ? $"No windows found ({policy.DescribeAllowed()})" : "No windows found";

    public override string Name => "windows_list_windows";

    public override string Description =>
        "List all open windows with their handles, titles, and process names. "
        + "Use this to find windows to interact with.";

    public override object InputSchema => new { type = "object", properties = new { } };

    public override Task<McpToolResult> ExecuteAsync(JsonElement? arguments)
    {
        try
        {
            var windows = _sessionManager.ListWindows();
            // Right after a launch or a closed dialog, windows can briefly be untitled; re-check before reporting none.
            for (var waited = 0; windows.Count == 0 && waited < 1000; waited += 250)
            {
                Thread.Sleep(250);
                windows = _sessionManager.ListWindows();
            }

            if (windows.Count == 0)
            {
                return Task.FromResult(TextResult(EmptyMessage(_policy)));
            }

            var lines = windows.Select(w => $"- {w.handle}: \"{w.title}\" ({w.processName ?? "unknown"})");

            return Task.FromResult(TextResult(string.Join("\n", lines)));
        }
        catch (Exception ex)
        {
            return Task.FromResult(ErrorResult($"Failed to list windows: {ex.Message}"));
        }
    }
}

/// <summary>
/// Focus a window
/// </summary>
public class FocusWindowTool : ToolBase
{
    private readonly SessionManager _sessionManager;

    public FocusWindowTool(SessionManager sessionManager)
    {
        _sessionManager = sessionManager;
    }

    public override string Name => "windows_focus";

    public override string Description => "Bring a window to the foreground and give it focus.";

    public override object InputSchema =>
        new
        {
            type = "object",
            properties = new
            {
                handle = new
                {
                    type = "string",
                    description = "Window handle from windows_list_windows or windows_launch",
                },
                title = new { type = "string", description = "Exact window title (alternative to handle)." },
            },
        };

    public override Task<McpToolResult> ExecuteAsync(JsonElement? arguments)
    {
        var handle = GetStringArgument(arguments, "handle");
        var title = GetStringArgument(arguments, "title");

        try
        {
            if (!string.IsNullOrEmpty(handle))
            {
                _sessionManager.FocusWindow(handle);
                return Task.FromResult(FocusResult(handle, _sessionManager.GetWindowHwnd(handle)));
            }
            else if (!string.IsNullOrEmpty(title))
            {
                var (windowHandle, window) = _sessionManager.AttachToWindow(title);
                _sessionManager.FocusWindow(windowHandle);
                return Task.FromResult(FocusResult(windowHandle, _sessionManager.GetWindowHwnd(windowHandle)));
            }
            else
            {
                return Task.FromResult(ErrorResult("Either 'handle' or 'title' is required"));
            }
        }
        catch (Exception ex)
        {
            return Task.FromResult(ErrorResult($"Failed to focus window: {ex.Message}"));
        }
    }

    private static McpToolResult FocusResult(string handle, nint hwnd) =>
        TextResult(
            hwnd != 0 && Win32Desktop.GetForegroundWindow() == hwnd
                ? $"Focused window {handle}"
                : $"Focus requested for {handle}, but foreground activation was not observed. Do not send input until the target is active."
        );
}

/// <summary>
/// Close a window
/// </summary>
public class CloseWindowTool : ToolBase
{
    private readonly SessionManager _sessionManager;

    public CloseWindowTool(SessionManager sessionManager)
    {
        _sessionManager = sessionManager;
    }

    public override string Name => "windows_close";

    public override string Description => "Close a window.";

    public override object InputSchema =>
        new
        {
            type = "object",
            properties = new { handle = new { type = "string", description = "Window handle to close" } },
            required = new[] { "handle" },
        };

    public override Task<McpToolResult> ExecuteAsync(JsonElement? arguments)
    {
        var handle = GetStringArgument(arguments, "handle");
        if (string.IsNullOrEmpty(handle))
        {
            return Task.FromResult(ErrorResult("Missing required argument: handle"));
        }

        try
        {
            _sessionManager.CloseWindow(handle);
            return Task.FromResult(
                TextResult(
                    $"Close requested for window {handle}. A save prompt may keep it open; use windows_list_windows to verify before continuing."
                )
            );
        }
        catch (Exception ex)
        {
            return Task.FromResult(ErrorResult($"Failed to close window: {ex.Message}"));
        }
    }
}
