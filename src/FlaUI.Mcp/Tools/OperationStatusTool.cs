using System.Text.Json;
using FlaUI.Mcp.Core;

namespace FlaUI.Mcp.Tools;

public sealed class OperationStatusTool(OperationCoordinator operations, PendingInvokeTracker pending) : ToolBase
{
    public override string Name => "windows_operation_status";
    public override string Description =>
        "Read-only status for running, timed-out, and completed operations. status is provider_pending while a click it started still blocks UI Automation on that app (usually an open menu or dialog). No UI Automation calls.";
    public override object InputSchema =>
        new { type = "object", properties = new { operationId = new { type = "string" } } };

    public override Task<McpToolResult> ExecuteAsync(JsonElement? arguments) =>
        Task.FromResult(
            TextResult(
                JsonSerializer.Serialize(
                    new
                    {
                        operations = operations.Status(GetStringArgument(arguments, "operationId"), pending),
                        pendingPatterns = pending.Status(),
                    }
                )
            )
        );
}
