using FlaUI.Mcp.Tools;

namespace FlaUI.Mcp.Core;

/// <summary>Owns shared services; the runner omits MCP-only window-management tools.</summary>
public sealed class AutomationHost : IDisposable
{
    public SessionManager Sessions { get; }
    public ElementRegistry Elements { get; } = new();
    public PendingInvokeTracker Pending { get; } = new();
    public ToolRegistry Tools { get; }

    public AutomationHost(ProcessPolicy policy, bool includeDesktopTools = false, Action? onToolActivity = null)
    {
        Sessions = new(policy);
        Tools = new(onToolActivity: onToolActivity);
        try
        {
            ToolComposition.Register(Tools, Sessions, Elements, Pending, policy, includeDesktopTools);
        }
        catch
        {
            Sessions.Dispose();
            throw;
        }
    }

    public void Dispose() => Sessions.Dispose();
}
