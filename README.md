# FlaUI-MCP

An MCP server that lets AI agents drive Windows desktop applications through UI Automation, the accessibility API screen readers use. It works the way Playwright's MCP server works for browsers: the agent reads an accessibility tree with element refs and acts on those refs, instead of guessing coordinates from screenshots.

This is the shared core. Product-specific automation builds on it in separate repositories, such as [FlaUI-MCP for Tabular Editor 3](https://github.com/eugman/FlaUI-MCP). It started as a fork of [shanselman/FlaUI-MCP](https://github.com/shanselman/FlaUI-MCP).

## Example

```
(abbreviated output)
windows_launch   {"app": "calc.exe"}                      → handle w1
windows_snapshot {"handle": "w1"}
  - window "Calculator" [ref=w1]
    - button "Three" [ref=w1e43]
    - button "Multiply by" [ref=w1e35]
    - button "Equals" [ref=w1e38]
    - text "Display is 0" [ref=w1e15]
windows_batch    {"actions": [{"action": "click", "ref": "w1e43"}, {"action": "click", "ref": "w1e35"},
                              {"action": "click", "ref": "w1e43"}, {"action": "click", "ref": "w1e38"}]}
```

## Install

You need Windows 10 or 11, the .NET 9 SDK (9.0.300 or later; see `global.json`) and the .NET 8 Desktop Runtime.

```powershell
git clone https://github.com/eugman/FlaUI-MCP-Core.git
cd FlaUI-MCP-Core
dotnet build FlaUI.Mcp.slnx -c Release
```

Then add the server to your MCP client:

```json
{
  "mcpServers": {
    "windows": {
      "command": "C:/path/to/FlaUI-MCP-Core/src/FlaUI.Mcp/bin/Release/net8.0-windows/FlaUI.Mcp.exe"
    }
  }
}
```

## Tools

| Tool | Does |
|---|---|
| `windows_launch` | Start an application and attach to its window. |
| `windows_list_windows` | List top-level windows and their handles. |
| `windows_focus` | Bring a window to the foreground. |
| `windows_close` | Ask a window to close. |
| `windows_place_window` | Move or resize a window. |
| `windows_snapshot` | Read a window's accessibility tree with element refs. |
| `windows_find` | Find elements by selector without reading the whole tree. |
| `windows_wait` | Wait until a selector matches, or stops matching. |
| `windows_get_text` | Read an element's text. |
| `windows_click` | Click or invoke an element. |
| `windows_fill` | Replace a field's value. |
| `windows_type` | Type text into an element or the focused control. |
| `windows_paste` | Paste text through the clipboard, which autocomplete can't rewrite. The pasted text stays on the clipboard. |
| `windows_send_keys` | Send keys or chords such as `Ctrl+A`. |
| `windows_get_clipboard` | Read the clipboard, for editors that hide their text from UI Automation. |
| `windows_screenshot` | Capture a window, element or screen region as PNG. |
| `windows_batch` | Run several actions in one call. |
| `windows_operation_status` | Check on an action that is still running (for example, one blocked by a modal dialog). |

Product hosts embed the core and may expose only some of these. `launch`, `list_windows`, `focus`, `close`, `get_text` and `batch` are the desktop-management tools that a host can leave out.

## Behaviour worth knowing

**Finding things.** `windows_find` and `windows_wait` accept `text:`, which matches an element's name or its value. Grid, tree and list rows often have positional names like `Node0` with the visible label in `value`, so prefer `text:` for anything you can see on screen.

**Modal dialogs.** A click that opens a modal dialog blocks the app's UI Automation provider until the dialog closes. `windows_click` notices this and returns with the dialog's title. While the click is pending, tools that need that app's provider fail fast with guidance; keyboard input by handle, window listing, focus and close still work. Don't replay the pending click: check it with `windows_operation_status`.

**Screenshots.**
- A normal capture copies screen pixels, so it is refused when another application covers the target. Native capture (`strictNative: true`) renders the window's own surface and isn't affected by what's in front of it.
- `background: true` captures by handle without bringing the window forward; it leaves out owned dialogs and open menus.
- `savePath` must be an absolute `.png` path. Existing files are kept unless you pass `overwrite: true`.
- `locate: ["w1e5"]` returns each element's rectangle in the captured image.
- The result reports window and monitor DPI and flags a mismatch.

**Batches.** `windows_batch` stops at a step whose action is still pending, even with `stopOnError: false`. Inspect the application before sending dependent input.

**Timeouts.** Every tool call times out after 30 seconds, so a hung provider returns an error instead of hanging the server. The action may still complete afterwards: observe before retrying.

**Snapshots** are bounded by `maxNodes` (default 3000) and `maxCharacters` (default 20000). A partial snapshot says so; a control missing from it is not proven absent.

## Configuration

| Environment variable | Effect |
|---|---|
| `FLAUI_MCP_ALLOWED_APPS` | Semicolon- or comma-separated process names (e.g. `notepad;calc`). Only these apps can be launched, listed, read, captured or sent input. Unset means any app. |
| `FLAUI_MCP_KEEP_AWAKE_SECONDS` | While tools are being called, the server keeps the display on and the lock screen away. It releases that after this many idle seconds (default 300). `0` turns it off. |

The allowlist guards against an agent being steered into the wrong app through this server. It matches by process name and is not a sandbox. Dialogs the allowed process owns keep working; separate processes it starts (such as a browser for sign-in) are blocked unless listed. While the allowlist is set, the Windows key is rejected.

## Develop

```powershell
pwsh scripts/setup.ps1      # once: formatter and git hooks
pwsh scripts/check.ps1      # formatting, build with analyzers, unit tests
```

`tests/FlaUI.Mcp.IntegrationTests` launches WinForms and WPF test apps and needs an interactive desktop. See [docs/code-quality.md](docs/code-quality.md) for the formatting and size rules, and [CONTRIBUTING.md](CONTRIBUTING.md) for how product repos take changes from this one.

## License

MIT. See [LICENSE](LICENSE).
