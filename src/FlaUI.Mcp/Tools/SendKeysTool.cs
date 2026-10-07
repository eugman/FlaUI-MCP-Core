using System.Text.Json;
using FlaUI.Core.Input;
using FlaUI.Core.WindowsAPI;
using FlaUI.Mcp.Core;

namespace FlaUI.Mcp.Tools;

/// <summary>
/// Send key presses and key chords to an element or the currently focused control.
/// </summary>
public class SendKeysTool : ToolBase
{
    private static readonly VirtualKeyShort[] ModifierKeys =
    {
        VirtualKeyShort.CONTROL,
        VirtualKeyShort.LCONTROL,
        VirtualKeyShort.RCONTROL,
        VirtualKeyShort.SHIFT,
        VirtualKeyShort.LSHIFT,
        VirtualKeyShort.RSHIFT,
        VirtualKeyShort.ALT,
        VirtualKeyShort.LMENU,
        VirtualKeyShort.RMENU,
        VirtualKeyShort.LWIN,
        VirtualKeyShort.RWIN,
    };

    private static readonly Dictionary<string, VirtualKeyShort> KeyMap = new(StringComparer.OrdinalIgnoreCase)
    {
        ["ctrl"] = VirtualKeyShort.CONTROL,
        ["control"] = VirtualKeyShort.CONTROL,
        ["shift"] = VirtualKeyShort.SHIFT,
        ["alt"] = VirtualKeyShort.ALT,
        ["menu"] = VirtualKeyShort.ALT,
        ["enter"] = VirtualKeyShort.ENTER,
        ["return"] = VirtualKeyShort.ENTER,
        ["tab"] = VirtualKeyShort.TAB,
        ["space"] = VirtualKeyShort.SPACE,
        ["esc"] = VirtualKeyShort.ESCAPE,
        ["escape"] = VirtualKeyShort.ESCAPE,
        ["left"] = VirtualKeyShort.LEFT,
        ["right"] = VirtualKeyShort.RIGHT,
        ["up"] = VirtualKeyShort.UP,
        ["down"] = VirtualKeyShort.DOWN,
        ["home"] = VirtualKeyShort.HOME,
        ["end"] = VirtualKeyShort.END,
        ["pageup"] = VirtualKeyShort.PRIOR,
        ["pgup"] = VirtualKeyShort.PRIOR,
        ["prior"] = VirtualKeyShort.PRIOR,
        ["pagedown"] = VirtualKeyShort.NEXT,
        ["pgdn"] = VirtualKeyShort.NEXT,
        ["next"] = VirtualKeyShort.NEXT,
        ["apps"] = VirtualKeyShort.APPS,
        ["contextmenu"] = VirtualKeyShort.APPS,
        ["media_next"] = VirtualKeyShort.MEDIA_NEXT_TRACK,
        ["media_prev"] = VirtualKeyShort.MEDIA_PREV_TRACK,
        ["media_play_pause"] = VirtualKeyShort.MEDIA_PLAY_PAUSE,
        ["media_stop"] = VirtualKeyShort.MEDIA_STOP,
        ["f1"] = VirtualKeyShort.F1,
        ["f2"] = VirtualKeyShort.F2,
        ["f3"] = VirtualKeyShort.F3,
        ["f4"] = VirtualKeyShort.F4,
        ["f5"] = VirtualKeyShort.F5,
        ["f6"] = VirtualKeyShort.F6,
        ["f7"] = VirtualKeyShort.F7,
        ["f8"] = VirtualKeyShort.F8,
        ["f9"] = VirtualKeyShort.F9,
        ["f10"] = VirtualKeyShort.F10,
        ["f11"] = VirtualKeyShort.F11,
        ["f12"] = VirtualKeyShort.F12,
        ["a"] = VirtualKeyShort.KEY_A,
        ["b"] = VirtualKeyShort.KEY_B,
        ["c"] = VirtualKeyShort.KEY_C,
        ["d"] = VirtualKeyShort.KEY_D,
        ["e"] = VirtualKeyShort.KEY_E,
        ["f"] = VirtualKeyShort.KEY_F,
        ["g"] = VirtualKeyShort.KEY_G,
        ["h"] = VirtualKeyShort.KEY_H,
        ["i"] = VirtualKeyShort.KEY_I,
        ["j"] = VirtualKeyShort.KEY_J,
        ["k"] = VirtualKeyShort.KEY_K,
        ["l"] = VirtualKeyShort.KEY_L,
        ["m"] = VirtualKeyShort.KEY_M,
        ["n"] = VirtualKeyShort.KEY_N,
        ["o"] = VirtualKeyShort.KEY_O,
        ["p"] = VirtualKeyShort.KEY_P,
        ["q"] = VirtualKeyShort.KEY_Q,
        ["r"] = VirtualKeyShort.KEY_R,
        ["s"] = VirtualKeyShort.KEY_S,
        ["t"] = VirtualKeyShort.KEY_T,
        ["u"] = VirtualKeyShort.KEY_U,
        ["v"] = VirtualKeyShort.KEY_V,
        ["w"] = VirtualKeyShort.KEY_W,
        ["x"] = VirtualKeyShort.KEY_X,
        ["y"] = VirtualKeyShort.KEY_Y,
        ["z"] = VirtualKeyShort.KEY_Z,
        ["0"] = VirtualKeyShort.KEY_0,
        ["1"] = VirtualKeyShort.KEY_1,
        ["2"] = VirtualKeyShort.KEY_2,
        ["3"] = VirtualKeyShort.KEY_3,
        ["4"] = VirtualKeyShort.KEY_4,
        ["5"] = VirtualKeyShort.KEY_5,
        ["6"] = VirtualKeyShort.KEY_6,
        ["7"] = VirtualKeyShort.KEY_7,
        ["8"] = VirtualKeyShort.KEY_8,
        ["9"] = VirtualKeyShort.KEY_9,
        ["backspace"] = VirtualKeyShort.BACK,
        ["bksp"] = VirtualKeyShort.BACK,
        ["delete"] = VirtualKeyShort.DELETE,
        ["del"] = VirtualKeyShort.DELETE,
        ["insert"] = VirtualKeyShort.INSERT,
        ["ins"] = VirtualKeyShort.INSERT,
        ["win"] = VirtualKeyShort.LWIN,
        ["windows"] = VirtualKeyShort.LWIN,
        ["meta"] = VirtualKeyShort.LWIN,
        ["printscreen"] = VirtualKeyShort.SNAPSHOT,
        ["prtsc"] = VirtualKeyShort.SNAPSHOT,
        ["prtscr"] = VirtualKeyShort.SNAPSHOT,
        ["pause"] = VirtualKeyShort.PAUSE,
        ["break"] = VirtualKeyShort.PAUSE,
    };

    private readonly ElementRegistry _elementRegistry;
    private readonly PendingInvokeTracker _invokeTracker;
    private readonly ProcessPolicy _processPolicy;
    private readonly SessionManager? _sessions;

    /// <summary>
    /// Initializes a new instance of the <see cref="SendKeysTool"/> class.
    /// </summary>
    /// <param name="elementRegistry">Registry used to resolve element references for focus targeting.</param>
    /// <param name="invokeTracker">Tracker used to fail fast when the target app's UIA provider is blocked.</param>
    /// <param name="processPolicy">Optional allowlist restricting which apps may receive input.</param>
    public SendKeysTool(
        ElementRegistry elementRegistry,
        PendingInvokeTracker? invokeTracker = null,
        ProcessPolicy? processPolicy = null,
        SessionManager? sessions = null
    )
    {
        _elementRegistry = elementRegistry;
        _invokeTracker = invokeTracker ?? new PendingInvokeTracker();
        _processPolicy = processPolicy ?? ProcessPolicy.AllowAll;
        _sessions = sessions;
    }

    /// <summary>
    /// Gets the MCP tool name.
    /// </summary>
    public override string Name => "windows_send_keys";

    /// <summary>
    /// Gets the MCP tool description.
    /// </summary>
    public override string Description =>
        "Send key presses or key chords to an element by ref or the focused element. "
        + "Supports either `chord` (single chord, e.g., Ctrl+Right) or `keys` array (sequence, e.g., [\"Ctrl+C\",\"Ctrl+V\"]).";

    /// <summary>
    /// Gets the JSON schema for tool inputs.
    /// </summary>
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
                verifyFocus = new
                {
                    type = "boolean",
                    description = "Require ref and verified keyboard focus on that exact control before the sequence (default false).",
                },
                expectFocusClass = new
                {
                    type = "string",
                    description = "Abort without sending unless the focused control's window class matches this exactly, "
                        + "rechecked before every chord. Use it for ref-less or handle-only input, which is otherwise "
                        + "unverified: the keys go wherever focus happens to be. Every reply names the focused class, so "
                        + "run once to discover it.",
                },
                @ref = new
                {
                    type = "string",
                    description = "Element ref from windows_snapshot (e.g., 'w1e5'). If omitted, sends to focused element.",
                },
                chord = new
                {
                    type = "string",
                    description = "Single key chord string, e.g. 'Ctrl+Right' or 'Alt+F4'.",
                },
                keys = new
                {
                    type = "array",
                    description = "Sequence of key presses/chords, e.g. ['Ctrl+C', 'Ctrl+V', 'Enter'].",
                    items = new { type = "string" },
                },
            },
        };

    /// <summary>
    /// Executes the key sending action.
    /// </summary>
    /// <param name="arguments">Tool arguments containing optional target ref and either chord or keys.</param>
    /// <returns>An MCP result with operation status or error details.</returns>
    public override Task<McpToolResult> ExecuteAsync(JsonElement? arguments)
    {
        var refId = GetStringArgument(arguments, "ref");
        var verifyFocus = GetBoolArgument(arguments, "verifyFocus");
        if (verifyFocus && string.IsNullOrWhiteSpace(refId))
        {
            return Task.FromResult(ErrorResult("verifyFocus requires an element ref; no input sent."));
        }

        var chord = GetStringArgument(arguments, "chord");
        var keyList = GetArgument<List<string>>(arguments, "keys");
        var hasChord = !string.IsNullOrWhiteSpace(chord);
        var hasKeys = keyList != null && keyList.Count > 0;

        if (!hasChord && !hasKeys)
        {
            return Task.FromResult(ErrorResult("Provide either chord or keys."));
        }

        if (hasChord && hasKeys)
        {
            return Task.FromResult(ErrorResult("Provide either chord or keys, not both."));
        }

        var completed = 0;
        var inputAttempted = false;
        try
        {
            OperationContext.Check();
            // Validate every chord before resolving or focusing any target. A malformed
            // later step must not allow an earlier shortcut (such as Save) to run.
            var prepared = PrepareSequence(hasChord ? new[] { chord! } : keyList!);
            var handle = GetStringArgument(arguments, "handle");
            if (handle != null && _sessions == null)
            {
                return Task.FromResult(ErrorResult("Window-handle input requires a session manager."));
            }

            if (!string.IsNullOrWhiteSpace(refId))
            {
                _elementRegistry.ResolveRef(refId, handle);

                // Fail fast if this app's UIA provider is blocked (element.Focus() would hang).
                // Tip: calling windows_send_keys without a ref sends pure keyboard input to the
                // focused element, which works even while the provider is blocked.
                if (_invokeTracker.TryGetPending(_elementRegistry.GetProcessIdForRef(refId), out var pending))
                {
                    return Task.FromResult(BlockedResult(pending));
                }
            }
            else if (handle != null)
            {
                // Validate the explicit target, then GuardedInput focuses and
                // verifies that exact window before sending any input.
                if (!_processPolicy.IsProcessAllowed(_sessions!.GetInputTarget(handle!).ProcessId))
                {
                    return Task.FromResult(ErrorResult(_processPolicy.DescribeDenied("Target process")));
                }
            }

            using var input = new GuardedInput(
                refId != null ? _elementRegistry.InputForRef(refId)
                    : handle != null ? _sessions!.GetInputTarget(handle)
                    : GuardedInput.ForegroundTarget(_processPolicy),
                refId == null ? null : _elementRegistry.GetElement(refId),
                verifyFocus
            );
            var targetName = string.IsNullOrWhiteSpace(refId) ? "focused element" : refId;
            var expectFocusClass = GetStringArgument(arguments, "expectFocusClass");
            var focus = Win32Desktop.GetFocusedControl();
            // Where the first chord went. The loop overwrites `focus` as the sequence moves it.
            var dispatchFocus = focus;
            foreach (var step in prepared)
            {
                // Rechecked every step, not once: the previous chord can move focus, and a sequence
                // that starts in the right control can finish somewhere else entirely.
                if (FocusPreconditionFailure(focus, expectFocusClass, _processPolicy) is { } mismatch)
                {
                    return Task.FromResult(ErrorResult(AbortMessage(mismatch, completed, prepared.Count)));
                }

                inputAttempted = true;
                input.Send(() => PressKeys(step.Keys));
                completed++;
                if (!hasChord)
                {
                    Thread.Sleep(30);
                }

                focus = Win32Desktop.GetFocusedControl();
            }

            // "focus at dispatch" is where the input went; the after-state is reported only when
            // it differs, because that difference is the thing worth noticing.
            var focusNote =
                DescribeFocus(dispatchFocus, _processPolicy, "focus at dispatch")
                + (focus == dispatchFocus ? "" : DescribeFocus(focus, _processPolicy, "focus afterwards"));
            return Task.FromResult(
                TextResult(
                    hasChord
                        ? $"Sent keys {prepared[0].Text} to {targetName}{focusNote}"
                        : $"Sent key sequence [{string.Join(", ", prepared.Select(step => step.Text))}] to {targetName}{focusNote}"
                )
            );
        }
        catch (Exception ex)
        {
            return Task.FromResult(
                ErrorResult(
                    $"Failed to send keys: {ex.Message} Completed {completed} chord(s). "
                        + (
                            inputAttempted
                                ? "A failing chord may have partially executed; inspect state before replaying input."
                                : "No keyboard input was dispatched."
                        )
                )
            );
        }
    }

    /// <summary>
    /// Describe where keyboard input actually landed. Ref-less and handle-only input goes to
    /// whatever holds focus, and reporting only "focused element" is the difference between a
    /// silent wrong-target send and a self-diagnosing one: two PageDowns aimed at a tree went into
    /// a code editor holding unsaved changes and the reply gave no way to notice.
    /// </summary>
    /// <param name="focus">Observed focus, or null when none was reported.</param>
    /// <param name="policy">App allowlist; a focused window outside it is not described.</param>
    /// <returns>A parenthesised suffix, or a note that focus was not reported.</returns>
    internal static string DescribeFocus(FocusedControl? focus, ProcessPolicy policy, string label = "focus")
    {
        if (focus == null)
        {
            return $" ({label}: not reported)";
        }
        // The foreground window can belong to any app; do not leak its title or class.
        if (!policy.IsProcessAllowed(focus.ProcessId))
        {
            return $" ({label}: a window outside the app allowlist)";
        }

        var window = string.IsNullOrEmpty(focus.RootTitle) ? "untitled window" : $"\"{focus.RootTitle}\"";
        var className = string.IsNullOrEmpty(focus.ClassName) ? "unknown class" : focus.ClassName;
        return $" ({label}: class={className} in {window})";
    }

    /// <summary>
    /// Check an opt-in focus precondition before any key is dispatched. Reporting the wrong target
    /// afterwards is diagnosis; refusing to send is prevention, which is what a caller about to
    /// type into the wrong control needs.
    /// </summary>
    /// <param name="focus">Observed focus, or null when none was reported.</param>
    /// <param name="expectedClass">Required window class, or null/blank to skip the check.</param>
    /// <param name="policy">App allowlist, used to avoid describing a disallowed window.</param>
    /// <returns>An error message when the send must be abandoned, otherwise null.</returns>
    internal static string? FocusPreconditionFailure(FocusedControl? focus, string? expectedClass, ProcessPolicy policy)
    {
        if (string.IsNullOrWhiteSpace(expectedClass))
        {
            return null;
        }
        // An unknown focus is not evidence the expectation holds.
        if (focus == null)
        {
            return "No focused control was reported, so expectFocusClass could not be checked.";
        }

        if (string.Equals(focus.ClassName, expectedClass, StringComparison.Ordinal))
        {
            return null;
        }

        return $"Focused control does not match expectFocusClass {expectedClass}." + DescribeFocus(focus, policy);
    }

    /// <summary>
    /// State what an aborted sequence did and did not dispatch. The diagnosis used to end with
    /// "No keyboard input was dispatched." unconditionally, so a sequence that stopped after two
    /// chords reported both that nothing was sent and that two chords ran — and the false half
    /// came first.
    /// </summary>
    /// <param name="mismatch">Why the precondition failed, with no claim about what was sent.</param>
    /// <param name="completed">Chords already dispatched.</param>
    /// <param name="total">Chords in the requested sequence.</param>
    /// <returns>The refusal, ending in an accurate account of what reached the desktop.</returns>
    internal static string AbortMessage(string mismatch, int completed, int total) =>
        completed == 0
            ? $"{mismatch} No keyboard input was dispatched."
            : $"{mismatch} Completed {completed} chord(s) before focus moved; the remaining "
                + $"{total - completed} were not sent.";

    internal List<(string Text, List<VirtualKeyShort> Keys)> PrepareSequence(IEnumerable<string> sequence)
    {
        var prepared = new List<(string, List<VirtualKeyShort>)>();
        foreach (var item in sequence)
        {
            var tokens = SplitChord(item).ToList();
            if (tokens.Count == 0)
            {
                throw new ArgumentException("No keys were parsed from chord.");
            }

            var keys = TryResolveKeys(tokens, out var error);
            if (error != null)
            {
                throw new ArgumentException(error);
            }

            var denied = CheckKeysAllowed(keys);
            if (denied != null)
            {
                throw new ArgumentException(denied);
            }

            prepared.Add((string.Join("+", tokens), keys));
        }

        return prepared;
    }

    /// <summary>
    /// The Windows key opens UI (Start menu, Win+R, Win+E, ...) that always
    /// belongs to processes outside any allowlist, so it is rejected while an
    /// allowlist is active.
    /// </summary>
    private string? CheckKeysAllowed(List<VirtualKeyShort> keys)
    {
        if (_processPolicy.IsRestricted && keys.Any(k => k is VirtualKeyShort.LWIN or VirtualKeyShort.RWIN))
        {
            return "The Windows key is disabled while the app allowlist is active, "
                + "because it opens system UI outside the allowed apps.";
        }

        return null;
    }

    private static List<VirtualKeyShort> TryResolveKeys(List<string> tokens, out string? error)
    {
        var keys = new List<VirtualKeyShort>();
        foreach (var token in tokens)
        {
            if (!TryMapKey(token, out var virtualKey))
            {
                error = $"Unsupported key: {token}. For literal text, use windows_type.";
                return keys;
            }

            keys.Add(virtualKey);
        }

        error = null;
        return keys;
    }

    internal static void PressKeys(List<VirtualKeyShort> keys)
    {
        var pressedModifiers = keys.Where(k => ModifierKeys.Contains(k)).ToList();
        try
        {
            if (keys.Count == 1)
            {
                Keyboard.Press(keys[0]);
                Thread.Sleep(10);
                Keyboard.Release(keys[0]);
                return;
            }

            Keyboard.TypeSimultaneously(keys.ToArray());
        }
        finally
        {
            foreach (var mod in pressedModifiers)
            {
                Keyboard.Release(mod);
            }
        }
    }

    private static bool TryMapKey(string token, out VirtualKeyShort key)
    {
        // "Page Down" and "PageDown" name the same key.
        var normalized = string.Concat(token.Where(c => !char.IsWhiteSpace(c)));
        return KeyMap.TryGetValue(normalized, out key);
    }

    private static IEnumerable<string> SplitChord(string? input)
    {
        if (string.IsNullOrWhiteSpace(input))
        {
            return Enumerable.Empty<string>();
        }

        return input
            .Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(part => !string.IsNullOrWhiteSpace(part));
    }
}
