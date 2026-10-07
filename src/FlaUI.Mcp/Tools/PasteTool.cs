using System.Text.Json;
using FlaUI.Core.WindowsAPI;
using FlaUI.Mcp.Core;

namespace FlaUI.Mcp.Tools;

/// <summary>
/// Paste text through the clipboard. Typed keystrokes go through editor autocomplete; a paste does not.
/// </summary>
public sealed class PasteTool(
    ElementRegistry elements,
    PendingInvokeTracker pending,
    ProcessPolicy policy,
    SessionManager? sessions
) : ToolBase
{
    public override string Name => "windows_paste";

    public override string Description =>
        "Paste text into an element by ref, or the focused element, with the clipboard and Ctrl+V. "
        + "Use it for code and multi-line text, which code editors autocomplete when typed. "
        + "Overwrites the clipboard; the app may still reformat what it receives.";

    public override object InputSchema =>
        new
        {
            type = "object",
            properties = new
            {
                handle = new
                {
                    type = "string",
                    description = "Optional explicit window handle; otherwise uses the focused element.",
                },
                @ref = new
                {
                    type = "string",
                    description = "Element ref from windows_snapshot or windows_find. If omitted, pastes into the focused element.",
                },
                verifyFocus = new
                {
                    type = "boolean",
                    description = "Require ref and verified keyboard focus on that exact control before pasting (default false). Worth setting for code editors: an unverified paste inserts text wherever focus happens to be.",
                },
                text = new { type = "string", description = "Text to paste" },
            },
            required = new[] { "text" },
        };

    public override Task<McpToolResult> ExecuteAsync(JsonElement? arguments)
    {
        var text = GetStringArgument(arguments, "text");
        if (text == null)
        {
            return Task.FromResult(ErrorResult("Missing required argument: text"));
        }

        var refId = GetStringArgument(arguments, "ref");
        try
        {
            OperationContext.Check();
            var handle = GetStringArgument(arguments, "handle");
            if (handle != null && sessions == null)
            {
                return Task.FromResult(ErrorResult("Window-handle input requires a session manager."));
            }

            if (!string.IsNullOrEmpty(refId))
            {
                elements.ResolveRef(refId, handle);
                if (pending.TryGetPending(elements.GetProcessIdForRef(refId), out var blocked))
                {
                    return Task.FromResult(BlockedResult(blocked));
                }
            }
            else if (handle != null && !policy.IsProcessAllowed(sessions!.GetInputTarget(handle).ProcessId))
            {
                return Task.FromResult(ErrorResult(policy.DescribeDenied("Target process")));
            }

            var verifyFocus = GetBoolArgument(arguments, "verifyFocus");
            if (verifyFocus && string.IsNullOrEmpty(refId))
            {
                return Task.FromResult(ErrorResult("verifyFocus requires an element ref; nothing was pasted."));
            }

            var element = refId == null ? null : elements.GetElement(refId);
            // Takes the input lease and activates the target. Focus is only *verified* when the
            // caller asks: GuardedInput otherwise checks the foreground process, which cannot tell a
            // tree from a code editor in the same app.
            using var input = new GuardedInput(
                refId != null ? elements.InputForRef(refId)
                    : handle != null ? sessions!.GetInputTarget(handle)
                    : GuardedInput.ForegroundTarget(policy),
                element,
                verifyFocus
            );
            // Clipboard text uses CRLF; edit controls don't break lines on a bare LF.
            var clipboardText = text.ReplaceLineEndings("\r\n");
            if (!Win32Desktop.SetClipboardText(clipboardText))
            {
                return Task.FromResult(
                    ErrorResult(
                        "The clipboard is in use by another process, so nothing was pasted. The target window was "
                            + "activated before this failed, so the desktop foreground may have changed."
                    )
                );
            }
            // Read before pasting: "the control contains the text" is not evidence on its own,
            // because a field that already contained it reads the same either way.
            var before = ReadValue(element);
            // Focus as it was when Ctrl+V was dispatched. Reading it afterwards names whatever the
            // paste moved focus to — an autocomplete popup, say — not where the text went.
            var dispatchFocus = Win32Desktop.GetFocusedControl();
            input.Send(() => SendKeysTool.PressKeys([VirtualKeyShort.CONTROL, VirtualKeyShort.KEY_V]));

            var target = string.IsNullOrEmpty(refId) ? "focused element" : refId;
            // Ctrl+V is already dispatched. Nothing below may report a failure to paste: a
            // read-back problem is missing evidence, not an unsent keystroke.
            string note;
            try
            {
                note =
                    SendKeysTool.DescribeFocus(dispatchFocus, policy, "focus at dispatch")
                    + ". The clipboard now holds that text."
                    + PasteEvidence(element, clipboardText, before);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                note =
                    $". The clipboard now holds that text. Not verified: reading back after the "
                    + $"paste failed ({ex.Message}).";
            }

            return Task.FromResult(TextResult($"Sent Ctrl+V for {clipboardText.Length} characters to {target}{note}"));
        }
        catch (ProviderBlockedException blocked)
        {
            // Keep the structured blocked result rather than flattening it into a message.
            return Task.FromResult(BlockedResult(blocked.Pending));
        }
        catch (Exception ex)
        {
            return Task.FromResult(ErrorResult($"Failed to paste: {ex.Message}"));
        }
    }

    /// <summary>
    /// Read the target back where it exposes a value, so the reply reports what arrived rather than
    /// only that Ctrl+V was sent. A read-only editor, or an app that binds Ctrl+V to something else,
    /// otherwise produces an identical success message.
    /// </summary>
    /// <param name="element">The pasted-into element, or null for a ref-less paste.</param>
    /// <param name="expected">The text placed on the clipboard.</param>
    /// <returns>A sentence confirming or questioning the result, or a note that it is unverifiable.</returns>
    internal static string PasteEvidence(
        FlaUI.Core.AutomationElements.AutomationElement? element,
        string expected,
        string? before,
        int settleMilliseconds = 500
    )
    {
        if (element == null)
        {
            return " Not verified: no ref was given, so there is nothing to read back.";
        }

        bool supported;
        try
        {
            supported = element.Patterns.Value.IsSupported;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return " Not verified: reading the control back failed.";
        }

        if (!supported)
        {
            // Scintilla and similar editors expose no text at all; windows_get_clipboard plus a
            // select-and-copy is the only way to see what they now contain.
            return " Not verified: this control exposes no value to read back.";
        }
        // Ctrl+V is posted, not processed, so an immediate read almost always still sees the old
        // value. Poll for the change rather than reporting the race as a failed paste.
        var deadline = Environment.TickCount64 + settleMilliseconds;
        string? observed;
        while (true)
        {
            observed = ReadValue(element);
            if (observed != before || Environment.TickCount64 >= deadline)
            {
                break;
            }

            Thread.Sleep(25);
        }

        return DescribeEvidence(before, observed, expected);
    }

    /// <summary>The value a control reports, or null when it exposes none or cannot be read.</summary>
    private static string? ReadValue(FlaUI.Core.AutomationElements.AutomationElement? element)
    {
        try
        {
            return element?.Patterns.Value.IsSupported == true
                ? element.Patterns.Value.Pattern.Value.ValueOrDefault
                : null;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return null;
        }
    }

    /// <summary>
    /// Judge a paste from the value before and after it. A containment test alone reported
    /// "Verified" for an empty paste, for text the control already held, and for a re-paste after
    /// a failed attempt — three ways to confirm a paste that never happened.
    /// </summary>
    /// <param name="before">The value read before Ctrl+V, or null when it could not be read.</param>
    /// <param name="observed">The value read after, or null when it could not be read.</param>
    /// <param name="expected">The text placed on the clipboard.</param>
    /// <returns>A sentence stating what the read-back does and does not establish.</returns>
    internal static string DescribeEvidence(string? before, string? observed, string expected)
    {
        if (observed == null)
        {
            return " Not verified: this control did not report a value.";
        }

        if (expected.Length == 0)
        {
            return " Not verified: empty text is indistinguishable from no paste.";
        }

        if (before == null)
        {
            return " Not verified: the value before the paste could not be read, so a match here "
                + "cannot be attributed to the paste.";
        }

        if (observed == before)
        {
            return " Warning: the control's value did not change, so the paste may not have applied.";
        }

        if (observed.Contains(expected, StringComparison.Ordinal))
        {
            return " Verified: the value changed and now contains the pasted text.";
        }

        return " Warning: the value changed but does not contain the pasted text; the app may have "
            + "reformatted it, or another paste landed first.";
    }
}
