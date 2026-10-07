using FlaUI.Mcp;
using FlaUI.Mcp.Core;
using FlaUI.Mcp.Tools;

DpiUtility.EnablePerMonitorV2();

// Optional app allowlist: when FLAUI_MCP_ALLOWED_APPS is set (semicolon- or
// comma-separated process names, e.g. "notepad;calc"), only those
// apps can be launched, listed, snapshotted, screenshotted or receive input.
var processPolicy = ProcessPolicy.FromEnvironment();

// While tools are actively being called, hold a Windows power availability
// request (display required) so the screen does not turn off and the lock
// screen does not interrupt a long automation run. Released after a sliding
// idle period. Set FLAUI_MCP_KEEP_AWAKE_SECONDS to change the idle period,
// or to 0 to disable.
var keepAwakeSeconds = 300;
if (int.TryParse(Environment.GetEnvironmentVariable("FLAUI_MCP_KEEP_AWAKE_SECONDS"), out var configuredSeconds))
{
    keepAwakeSeconds = configuredSeconds;
}

using var keepAwake =
    keepAwakeSeconds > 0
        ? KeepAwake.CreateDisplayKeepAwake(
            TimeSpan.FromSeconds(keepAwakeSeconds),
            "FlaUI-MCP is driving Windows UI automation"
        )
        : null;

// MCP exposes the shared runner tools plus explicit window-management extensions.
using var host = new AutomationHost(
    processPolicy,
    includeDesktopTools: true,
    onToolActivity: keepAwake != null ? keepAwake.Poke : null
);
var sessionManager = host.Sessions;
var toolRegistry = host.Tools;

// Create and run MCP server
var server = new McpServer(toolRegistry);

using var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;
    cts.Cancel();
};

await server.RunAsync(cts.Token);
