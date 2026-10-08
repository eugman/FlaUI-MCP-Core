# Changelog

All notable changes to FlaUI-MCP will be documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.0.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [Unreleased]

### Changed
- This repository is now the shared core. Product automation (Tabular Editor 3, and others) lives in separate repos that merge from here.
- The namespace is `FlaUI.Mcp` (was `PlaywrightWindows.Mcp`), and the server reports itself to MCP clients as `flaui-mcp`.
- Added a clipboard lease (`Win32Desktop.PreserveAndSetClipboardText`) for hosts that can confirm a paste landed: it saves the user's text, writes ours, and restores theirs on dispose. `windows_paste` does not use it and still leaves the pasted text on the clipboard.
- Guarded input now refuses input when a different top-level window of the target process is in the foreground (for example an unowned dialog), not only when another process is. Owned dialogs and hosted children such as WebView2 still count as the target.
- Added settled-text reads (`ControlText`) and `Invoke` / `ExpandCollapse` as `windows_find` pattern filters.
- `windows_wait` accepts at most 25 seconds, so a maximum wait ends as a wait result rather than a tool timeout.
- Formatting, size and complexity limits are enforced by CSharpier, analyzers, git hooks and CI (see docs/code-quality.md).

### Fixed
- Guarded input accepts a modal dialog that disables the target even when the dialog is an owned overlapped window (WinForms `ShowDialog`), which `GA_ROOTOWNER` does not follow. A modeless owned window is still treated as a separate window. When the target is disabled by a modal, activation goes to the dialog rather than the disabled window.
- `windows_wait` rejects `settleMs` equal to `timeoutMs`, which could never succeed.
- The tool timeout timer is cancelled when a tool returns.
- `scripts/check.ps1` builds with two nodes, and `scripts/setup.ps1` only configures the blame ignore list when the repo has one.
- Integration tests: an exact-ID miss expects `ElementNotFoundException`, and tab selection names the `TabItem`, since a selected WinForms `TabPage` shares its tab's AutomationId.

### Added
- `Win32Desktop.IsMinimized` and `Win32Desktop.LastActivePopup`.

### Added
- `windows_screenshot` refuses a screen-pixel capture when another application's window covers part of the target, naming each obstruction by title, HWND and PID. Such a capture previously returned the covering window's pixels and reported success with `screenFallback: false` — observed three times against a real app, once by accident during ordinary work. Native capture (`strictNative`) renders the window's own surface and is exempt, as is the blocked-provider fallback, where a modal is in front of the target by construction and refusing would leave no way to capture at all. `allowOccludedPixels: true` overrides deliberately. Windows belonging to the target's own process are allowed, so an app's own dialogs and menus are never treated as intruders.
- `windows_screenshot` reports `monitorDpi` beside `windowDpi`, with a `dpiMismatch` flag. A window that has not re-negotiated after a display-scaling change renders a native capture at its own scale into a bitmap sized for the monitor's: correctly sized, wrongly scaled, and invisible to the `frame` size assertion because the bitmap is exactly the expected size. A mismatch warns and never refuses — those pixels are what is actually on the glass, and the state is permanent for DPI-unaware apps. Warnings no longer depend on `includeMetadata`, and `occluded` is tri-state so a capture that could not be checked is never reported as clean.
- `windows_screenshot` accepts `locate: ["ref", ...]` and returns each element's rectangle as measured in the returned image, so annotation coordinates and pixels come from one observation. Converting `windows_find` bounds by hand used screen coordinates from an earlier observation and put callout boxes on the wrong control; the offset that conversion needs is not even constant, measuring 39px in one DPI state and 55px in another on the same window. It refuses rather than guessing when the capture cannot be trusted to map 1:1.
- A `text:` selector on `windows_find` and `windows_wait` matching name **or** value. Grid, tree and list rows carry a positional name (`Node0`, `Name row 87`) with the displayed label in `value`, so a name-based search silently matches nothing. The value arm mirrors the existing `IsPassword` guard.
- `windows_send_keys` names the control that actually received the keys, and accepts `expectFocusClass` to abort before sending when focus is not where the caller expects, rechecked before every chord. Ref-less and handle-only input previously reported only "focused element": two keystrokes aimed at a tree landed in a code editor holding unsaved changes.
- `windows_get_clipboard` reads the clipboard, for controls that expose no text to UI Automation at all — a Scintilla editor has no Value or Text pattern, so selecting and copying is the only way to read one. While an app allowlist is active the text must have been put there by an allowed app — the gate reads `GetClipboardOwner`, since the clipboard is shared with every application and the *focused* window says nothing about who wrote to it.
- Blocked-provider and modal-detected guidance messages now recommend capturing the dialog via its own window handle from `windows_list_windows` (which works while the provider is blocked and under an app allowlist) instead of `fullScreen: true` (refused while an allowlist is active), and mention waiting for the dialog to appear and take focus before sending keys.
- Optional app allowlist via the `FLAUI_MCP_ALLOWED_APPS` environment variable (semicolon- or comma-separated process names). When set, the server refuses to launch, list, snapshot, screenshot or send input to any other application: window handles are only issued for allowed processes (scoping every ref-based tool), ref-less keyboard input requires an allowed foreground window and rejects the Windows key, and `fullScreen` screenshots are disabled. Unset means everything is allowed, as before.
- The server now keeps the display awake while tools are actively being called, so Windows does not turn off the screen or show the lock screen in the middle of a long automation run. Implemented with a Windows power availability request (`PowerCreateRequest`/`PowerSetRequest` with `PowerRequestDisplayRequired` + `PowerRequestSystemRequired`) — the same mechanism video players and conferencing apps use, visible in `powercfg /requests`. The request is released after 5 minutes without a tool call; configure the idle period (or disable with `0`) via the `FLAUI_MCP_KEEP_AWAKE_SECONDS` environment variable.

### Fixed
- `windows_click` no longer hangs (and then times out) when the clicked element's handler opens a modal dialog. UIA pattern calls (Invoke/Toggle/Select) are synchronous cross-process calls: a WinForms/DevExpress handler that calls `ShowDialog()` does not return until the dialog closes, blocking the target app's entire UIA provider. The click now runs on a background thread while non-blocking Win32 APIs watch for the modal signature (new top-level window, or owner window disabled) and returns immediately with the dialog's title and interaction guidance.
- While an app's UIA provider is blocked by such a pending call, `windows_snapshot`, `windows_get_text`, `windows_click`, `windows_type`, `windows_fill`, `windows_send_keys` (ref-based) and `windows_batch` actions targeting that app now fail fast with guidance (use `windows_screenshot` / `windows_send_keys` without ref) instead of hanging until the 30s global timeout.
- `windows_list_windows` now enumerates windows via Win32 instead of walking the UIA desktop tree, so it keeps working even while some app's UIA provider is blocked. Window handles are also stable across calls now (previously every call registered new handles for the same windows).
- `windows_focus` and `windows_close` (by handle) now use Win32 (`SetForegroundWindow` / `WM_CLOSE`) and work while a provider is blocked.
- `windows_screenshot` with a window handle falls back to a Win32 window-bounds capture while the app's provider is blocked.

## [0.2.0] - 2026-07-08

### Fixed
- Screenshots are now correct on scaled displays (DPI > 100%). Added `SetProcessDpiAwarenessContext(PER_MONITOR_AWARE_V2)` as the first call in the process entry point so UIA coordinates match physical pixels.
- Tool execution now times out after 30 seconds instead of hanging indefinitely when UI Automation blocks.

### Added
- Solution file (`FlaUI.Mcp.slnx`)
- xUnit test project (`tests/FlaUI.Mcp.Tests`) with DPI regression test
- `windows_send_keys` MCP tool for sending key presses and key chords.
- Opt-in `background` mode for `windows_screenshot` handle captures, with blank-frame fallback to the normal capture path.
- `savePath` and `overwrite` options for `windows_screenshot`, with local PNG path validation and atomic writes.
- Desktop integration test project with WinForms and WPF test applications.

## [0.1.0] - 2024-02-02

### Added
- Initial release
- **Core MCP Tools:**
  - `windows_launch` - Launch Windows applications
  - `windows_snapshot` - Capture accessibility tree with element refs
  - `windows_click` - Click elements by ref (uses Invoke pattern when available)
  - `windows_type` - Type text into elements
  - `windows_fill` - Clear and fill text fields
  - `windows_get_text` - Get element text content
  - `windows_screenshot` - Capture window/element screenshots
  - `windows_list_windows` - List all open windows
  - `windows_focus` - Bring window to foreground
  - `windows_close` - Close windows
  - `windows_batch` - Execute multiple actions in a single call

- **Architecture:**
  - MCP protocol handler (JSON-RPC over stdio)
  - Element registry for ref ↔ AutomationElement mapping
  - Snapshot builder for agent-friendly accessibility tree format
  - Session manager for tracking launched applications

- **Documentation:**
  - README with installation and usage instructions
  - GitHub Actions for CI/CD
  - MIT License

### Technical Details
- Built on [FlaUI](https://github.com/FlaUI/FlaUI) for Windows UI Automation
- Uses UIA3 for modern app support (WPF, UWP, Win32)
- Targets .NET 8.0-windows
- Prefers control patterns (Invoke, Value, Toggle) over mouse simulation
