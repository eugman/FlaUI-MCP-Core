using System.Diagnostics;
using System.Text.Json;
using FlaUI.Mcp.Core;
using FlaUI.Mcp.Tools;
using Xunit.Abstractions;

namespace FlaUI.Mcp.IntegrationTests;

/// <summary>
/// Tests for modal-dialog handling: a click whose handler opens a modal dialog
/// must not hang the click tool, and other tools must fail fast (with guidance)
/// instead of hanging while the app's UIA provider is blocked.
/// </summary>
[Collection("TestApps")]
public class ModalDialogTests
{
    private readonly TestAppFixture _fixture;
    private readonly ITestOutputHelper _output;

    public ModalDialogTests(TestAppFixture fixture, ITestOutputHelper output)
    {
        _fixture = fixture;
        _output = output;
    }

    [Fact]
    public async Task WinForms_ClickOpensModal_ReturnsEarlyAndFailsFastUntilDismissed()
    {
        var tracker = new PendingInvokeTracker();
        var clickTool = new ClickTool(_fixture.Elements, tracker);
        var snapshotTool = new SnapshotTool(_fixture.Session, _fixture.Elements, tracker);
        var sendKeysTool = new SendKeysTool(_fixture.Elements, tracker, sessions: _fixture.Session);

        // Capture the pid while the provider is responsive; querying it later,
        // while the modal blocks the provider, would itself hang
        var processId = GetWinFormsProcessId();
        Assert.NotEqual(0, processId);

        // Navigate to the Dialogs tab
        var dialogsTabRef = _fixture.FindRefByName(_fixture.WinFormsHandle, "Dialogs");
        Assert.NotNull(dialogsTabRef);
        await _fixture.CallTool(clickTool, new { @ref = dialogsTabRef });
        await Task.Delay(250);

        var modalButtonRef = _fixture.FindRefByName(_fixture.WinFormsHandle, "Open Modal Dialog");
        Assert.NotNull(modalButtonRef);

        try
        {
            // 1. Clicking the button must return quickly and report the modal
            var sw = Stopwatch.StartNew();
            var clickResult = await _fixture.CallTool(clickTool, new { @ref = modalButtonRef });
            sw.Stop();
            _output.WriteLine($"Click result ({sw.ElapsedMilliseconds}ms): {clickResult}");

            Assert.True(sw.Elapsed < TimeSpan.FromSeconds(10), $"Click took too long: {sw.Elapsed}");
            Assert.Contains("modal dialog", clickResult, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("Test Modal Dialog", clickResult);

            // 2. While the dialog is open, snapshot must fail fast with guidance
            //    instead of hanging until the global timeout
            sw.Restart();
            var snapshotResult = await _fixture.CallTool(snapshotTool, new { handle = _fixture.WinFormsHandle });
            sw.Stop();
            _output.WriteLine($"Snapshot result ({sw.ElapsedMilliseconds}ms): {snapshotResult}");

            Assert.True(sw.Elapsed < TimeSpan.FromSeconds(5), $"Snapshot fail-fast took too long: {sw.Elapsed}");
            Assert.Contains("blocked", snapshotResult, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("windows_send_keys", snapshotResult);
            var modal = Win32Desktop.GetTopLevelWindows(processId).Single(w => w.Title == "Test Modal Dialog");
            Assert.Equal(modal.Hwnd, Win32Desktop.GetForegroundWindow());
            using (var input = new GuardedInput(_fixture.Session.GetInputTarget(_fixture.WinFormsHandle)))
            {
                input.Verify();
                Assert.Equal(modal.Hwnd, Win32Desktop.GetForegroundWindow());
            }

            // An allowlist must not force strict native capture or block the modal's
            // own handle merely because an Invoke in its process is still pending.
            using var process = Process.GetProcessById(processId);
            var screenshots = new ScreenshotTool(
                _fixture.Session,
                _fixture.Elements,
                tracker,
                new ProcessPolicy([process.ProcessName])
            );
            var modalHandle = _fixture.Session.RegisterNativeWindow(modal.Hwnd, processId);
            var capture = await screenshots.ExecuteAsync(
                JsonSerializer.SerializeToElement(new { handle = modalHandle, background = true })
            );
            Assert.False(capture.IsError == true, string.Join("; ", capture.Content.Select(c => c.Text)));
            var image = Assert.Single(capture.Content.Where(c => c.Type == "image"));
            using var pixels = new MemoryStream(Convert.FromBase64String(image.Data!));
            using var bitmap = new System.Drawing.Bitmap(pixels);
            Assert.True(bitmap.Width > 0 && bitmap.Height > 0);
        }
        finally
        {
            // 3. Dismiss the dialog with pure keyboard input (works while blocked):
            //    the OK button is the dialog's accept button
            var dialog = Win32Desktop.GetTopLevelWindows(processId).Single(w => w.Title == "Test Modal Dialog");
            var handle = _fixture.Session.RegisterNativeWindow(dialog.Hwnd, processId);
            var dismissed = await _fixture.CallTool(sendKeysTool, new { handle, chord = "Enter" });
            Assert.Contains("Sent keys", dismissed);
        }

        // 4. Once the dialog closes, the pending invoke completes and tools recover
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        while (tracker.TryGetPending(processId, out _) && DateTime.UtcNow < deadline)
        {
            await Task.Delay(100);
        }

        Assert.False(
            tracker.TryGetPending(processId, out _),
            "Pending invoke did not clear after the dialog was dismissed."
        );

        var recoveredSnapshot = await _fixture.CallTool(snapshotTool, new { handle = _fixture.WinFormsHandle });
        Assert.Contains("Open Modal Dialog", recoveredSnapshot);
    }

    [Fact]
    public async Task WinForms_EllipsisButtonOpensModalWithoutBlockingTheProvider()
    {
        var tracker = new PendingInvokeTracker();
        var clickTool = new ClickTool(_fixture.Elements, tracker);
        var snapshotTool = new SnapshotTool(_fixture.Session, _fixture.Elements, tracker);
        var sendKeysTool = new SendKeysTool(_fixture.Elements, tracker, sessions: _fixture.Session);
        var processId = GetWinFormsProcessId();
        Assert.NotEqual(0, processId);

        var dialogsTabRef = _fixture.FindRefByName(_fixture.WinFormsHandle, "Dialogs");
        Assert.NotNull(dialogsTabRef);
        await _fixture.CallTool(clickTool, new { @ref = dialogsTabRef });
        await Task.Delay(250);
        var buttonRef = _fixture.FindRefByName(_fixture.WinFormsHandle, "Choose Options...");
        Assert.NotNull(buttonRef);

        try
        {
            // No physical argument: the "..." name selects a physical click instead of Invoke.
            var clickResult = await _fixture.CallTool(clickTool, new { @ref = buttonRef });
            _output.WriteLine($"Click result: {clickResult}");
            Assert.Contains("Clicked Choose Options...", clickResult);

            Win32WindowInfo? modal = null;
            var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
            while (
                (
                    modal = Win32Desktop
                        .GetTopLevelWindows(processId)
                        .SingleOrDefault(w => w.Title == "Test Modal Dialog")
                ) == null
                && DateTime.UtcNow < deadline
            )
            {
                await Task.Delay(100);
            }

            Assert.NotNull(modal);
            Assert.False(tracker.TryGetPending(processId, out _));

            // The provider stays free, so the dialog itself can be inspected.
            var modalHandle = _fixture.Session.RegisterNativeWindow(modal!.Hwnd, processId);
            var snapshot = await _fixture.CallTool(snapshotTool, new { handle = modalHandle });
            _output.WriteLine($"Dialog snapshot: {snapshot}");
            Assert.Contains("This is a test modal dialog.", snapshot);
        }
        finally
        {
            var dialog = Win32Desktop
                .GetTopLevelWindows(processId)
                .SingleOrDefault(w => w.Title == "Test Modal Dialog");
            if (dialog != null)
            {
                var handle = _fixture.Session.RegisterNativeWindow(dialog.Hwnd, processId);
                Assert.Contains("Sent keys", await _fixture.CallTool(sendKeysTool, new { handle, chord = "Enter" }));
                await Task.Delay(250);
            }
        }
    }

    [Fact]
    public async Task WinForms_ClickOpensModeless_CompletesNormally()
    {
        var tracker = new PendingInvokeTracker();
        var clickTool = new ClickTool(_fixture.Elements, tracker);
        var sendKeysTool = new SendKeysTool(_fixture.Elements, tracker, sessions: _fixture.Session);

        var processId = GetWinFormsProcessId();
        Assert.NotEqual(0, processId);

        // Navigate to the Dialogs tab
        var dialogsTabRef = _fixture.FindRefByName(_fixture.WinFormsHandle, "Dialogs");
        Assert.NotNull(dialogsTabRef);
        await _fixture.CallTool(clickTool, new { @ref = dialogsTabRef });
        await Task.Delay(250);

        var modelessButtonRef = _fixture.FindRefByName(_fixture.WinFormsHandle, "Open Modeless Dialog");
        Assert.NotNull(modelessButtonRef);

        try
        {
            // Show() returns immediately, so the invoke completes; the result may
            // legitimately be either a plain completion or (on a slow machine) a
            // modal-detection race, but it must never take the full grace period path
            var clickResult = await _fixture.CallTool(clickTool, new { @ref = modelessButtonRef });
            _output.WriteLine($"Click result: {clickResult}");
            Assert.Contains("Invoked", clickResult);

            // Provider is not blocked: no pending invoke remains after a moment
            var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
            while (tracker.TryGetPending(processId, out _) && DateTime.UtcNow < deadline)
            {
                await Task.Delay(50);
            }

            Assert.False(tracker.TryGetPending(processId, out _));
        }
        finally
        {
            // Close the modeless dialog (it has focus; Alt+F4 closes it)
            var dialog = Win32Desktop.GetTopLevelWindows(processId).Single(w => w.Title == "Test Modeless Dialog");
            var handle = _fixture.Session.RegisterNativeWindow(dialog.Hwnd, processId);
            var dismissed = await _fixture.CallTool(sendKeysTool, new { handle, chord = "Alt+F4" });
            Assert.Contains("Sent keys", dismissed);
            await Task.Delay(250);
        }
    }

    private int GetWinFormsProcessId()
    {
        var window = _fixture.GetWinFormsWindow();
        return window?.Properties.ProcessId.ValueOrDefault ?? 0;
    }
}
