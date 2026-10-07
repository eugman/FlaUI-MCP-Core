using FlaUI.Mcp.Tools;

namespace FlaUI.Mcp.Core;

/// <summary>Shared tool instances and explicit MCP-only discovery, launch and batch extensions.</summary>
internal static class ToolComposition
{
    public static void Register(
        ToolRegistry registry,
        SessionManager sessions,
        ElementRegistry elements,
        PendingInvokeTracker pending,
        ProcessPolicy policy,
        bool includeDesktopTools
    )
    {
        registry.ResolveTarget = arguments => sessions.ResolveTarget(arguments, elements);
        registry.Pending = pending;
        var query = new ElementQuery(sessions, elements, pending);
        registry.RegisterTool(new OperationStatusTool(registry.Operations, pending));
        registry.RegisterTool(new FindTool(query));
        registry.RegisterTool(new WaitTool(query));
        registry.RegisterTool(new SnapshotTool(sessions, elements, pending));
        registry.RegisterTool(new ClickTool(elements, pending));
        registry.RegisterTool(new FillTool(elements, pending));
        registry.RegisterTool(new TypeTool(elements, pending, policy, sessions));
        registry.RegisterTool(new PasteTool(elements, pending, policy, sessions));
        // Shared, not desktop-only: product hosts read back editors that expose no text to UI
        // Automation (e.g. Scintilla) to verify what they pasted.
        registry.RegisterTool(new GetClipboardTool(policy));
        registry.RegisterTool(new SendKeysTool(elements, pending, policy, sessions));
        registry.RegisterTool(new ScreenshotTool(sessions, elements, pending, policy));
        registry.RegisterTool(new WindowPlacementTool(elements, pending, sessions));
        if (includeDesktopTools)
        {
            registry.RegisterTool(new LaunchTool(sessions));
            registry.RegisterTool(new GetTextTool(elements, pending));
            registry.RegisterTool(new ListWindowsTool(sessions, policy));
            registry.RegisterTool(new FocusWindowTool(sessions));
            registry.RegisterTool(new CloseWindowTool(sessions));
            registry.RegisterTool(new BatchTool(registry));
        }
    }
}
