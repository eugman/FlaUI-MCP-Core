using System.Text.Json;
using FlaUI.Mcp.Core;

namespace FlaUI.Mcp.Tools;

/// <summary>
/// Read the clipboard. Some editors expose no text to UI Automation at all — a Scintilla-based one
/// has no Value or Text pattern — so selecting and copying is the only way to read
/// their contents.
/// </summary>
/// <param name="policy">App allowlist governing whose clipboard text may be returned.</param>
/// <param name="clipboardOwner">Clipboard-origin probe; overridden in tests, which have no desktop.</param>
public sealed class GetClipboardTool(ProcessPolicy? policy = null, Func<int>? clipboardOwner = null) : ToolBase
{
    private readonly ProcessPolicy _processPolicy = policy ?? ProcessPolicy.AllowAll;
    private readonly Func<int> _clipboardOwner = clipboardOwner ?? Win32Desktop.GetClipboardOwnerProcessId;

    public override string Name => "windows_get_clipboard";

    public override string Description =>
        "Read the Windows clipboard's text. Mainly for controls that expose no text to UI Automation, "
        + "such as Scintilla editors: focus one, select all, copy, then read it here. While an app "
        + "allowlist is active the text must have been put there by an allowed app, since the clipboard "
        + "is shared with every other application.";

    public override object InputSchema => new { type = "object", properties = new { } };

    public override Task<McpToolResult> ExecuteAsync(JsonElement? arguments)
    {
        try
        {
            OperationContext.Check();
            if (OwnerDenied(_processPolicy, _clipboardOwner()) is { } denial)
            {
                return Task.FromResult(ErrorResult(denial));
            }

            var clipboard = Win32Desktop.GetClipboardText();
            // "Could not read it" is not "there is nothing there"; the old code returned the same
            // sentence for both, so a clipboard held open by another process read as empty.
            if (clipboard.Failure != null)
            {
                return Task.FromResult(ErrorResult(clipboard.Failure));
            }

            if (clipboard.Text == null)
            {
                return Task.FromResult(TextResult("The clipboard holds no text."));
            }

            return Task.FromResult(
                TextResult(
                    clipboard.Truncated
                        ? clipboard.Text + "\n\n[Truncated: the clipboard holds more text than this.]"
                        : clipboard.Text
                )
            );
        }
        catch (Exception ex)
        {
            return Task.FromResult(ErrorResult($"Failed to read clipboard: {ex.Message}"));
        }
    }

    /// <summary>
    /// Refuse unless an allowed app put the text on the clipboard. The focused window is not a
    /// proxy for that: this server's own windows_focus can move focus to an allowed app and make
    /// any clipboard readable, so a focus check gates nothing. The clipboard owner is the process
    /// that last wrote to it, which is the question worth asking.
    /// </summary>
    /// <param name="policy">App allowlist; an unrestricted policy permits every read.</param>
    /// <param name="ownerProcessId">Clipboard owner, or 0 when it cannot be identified.</param>
    /// <returns>A refusal, or null when the read is permitted.</returns>
    internal static string? OwnerDenied(ProcessPolicy policy, int ownerProcessId)
    {
        if (!policy.IsRestricted)
        {
            return null;
        }
        // Text this server placed there itself, by windows_paste or windows_set_clipboard.
        if (ownerProcessId == Environment.ProcessId)
        {
            return null;
        }

        if (ownerProcessId == 0)
        {
            return "The process that put this text on the clipboard could not be identified, so it "
                + "cannot be checked against the app allowlist. Copy again from the app being automated.";
        }

        return policy.IsProcessAllowed(ownerProcessId)
            ? null
            : policy.DescribeDenied("The process that last wrote to the clipboard")
                + " The clipboard is shared with every application, so its text is only readable here "
                + "when an allowed app put it there.";
    }
}
