using System.Diagnostics;
using FlaUI.Mcp.Core;
using FlaUI.Mcp.Tools;
using Xunit;

namespace FlaUI.Mcp.Tests;

public sealed class ForegroundAuthorizationTests
{
    [Fact]
    public void WindowsHostedOrOwnedByTheTargetBelongToIt()
    {
        // GA_ROOT = 2 (parent chain), GA_ROOTOWNER = 3 (owner chain).
        nint Ancestor(nint hwnd, uint kind) =>
            (hwnd, kind) switch
            {
                (400, 2) => 100, // cross-process WebView2 child parented to the target
                (500, 3) => 100, // popup owned by the target
                _ => hwnd,
            };
        Assert.True(Win32Desktop.BelongsTo(100, 100, Ancestor));
        Assert.True(Win32Desktop.BelongsTo(400, 100, Ancestor));
        Assert.True(Win32Desktop.BelongsTo(500, 100, Ancestor));
        Assert.False(Win32Desktop.BelongsTo(900, 100, Ancestor));
        Assert.False(Win32Desktop.BelongsTo(0, 100, Ancestor));
        Assert.False(Win32Desktop.BelongsTo(400, 0, Ancestor));
    }

    [Fact]
    public void InputVerificationAcceptsOwnedWindowsAndRejectsSeparateWindows()
    {
        var target = new InputTarget(100, 7, 0);
        int Process(nint hwnd) =>
            hwnd switch
            {
                100 or 500 or 600 => 7,
                400 => 8,
                900 => 9,
                _ => 0,
            };
        bool Belongs(nint hwnd, nint owner) => hwnd == owner || (hwnd is 400 or 500 && owner == 100);
        static bool NotOwned(nint hwnd, nint owner) => false;

        Assert.True(GuardedInput.ForegroundAcceptable(target, 100, Process, Belongs, _ => true, NotOwned));
        Assert.True(GuardedInput.ForegroundAcceptable(target, 400, Process, Belongs, _ => true, NotOwned));
        Assert.True(GuardedInput.ForegroundAcceptable(target, 500, Process, Belongs, _ => true, NotOwned));
        Assert.False(GuardedInput.ForegroundAcceptable(target, 600, Process, Belongs, _ => true, NotOwned));
        Assert.False(GuardedInput.ForegroundAcceptable(target, 900, Process, Belongs, _ => true, NotOwned));
        Assert.False(GuardedInput.ForegroundAcceptable(target, 400, Process, Belongs, _ => false, NotOwned));
        Assert.False(GuardedInput.ForegroundAcceptable(target, 0, Process, Belongs, _ => true, NotOwned));
        Assert.False(
            GuardedInput.ForegroundAcceptable(target with { ProcessId = 8 }, 100, Process, Belongs, _ => true, NotOwned)
        );
    }

    [Fact]
    public void ModalOwnedWindowCountsOnlyWhileItDisablesTheTarget()
    {
        // 700: a WinForms dialog owned by 100 but not a WS_POPUP, so GA_ROOTOWNER misses it.
        var target = new InputTarget(100, 7, 0);
        static int Process(nint hwnd) => 7;
        static bool Belongs(nint hwnd, nint owner) => hwnd == owner;
        static bool Owned(nint hwnd, nint owner) => hwnd == 700 && owner == 100;

        Assert.True(GuardedInput.ForegroundAcceptable(target, 700, Process, Belongs, h => h != 100, Owned));
        // Modeless: the owner stays enabled, so the dialog is a separate window.
        Assert.False(GuardedInput.ForegroundAcceptable(target, 700, Process, Belongs, _ => true, Owned));
        Assert.False(GuardedInput.ForegroundAcceptable(target, 800, Process, Belongs, h => h != 100, Owned));
    }

    [Fact]
    public void InputAuthorizesTheCapturedProcessBeforeReturningItsTarget()
    {
        using var process = Process.GetCurrentProcess();
        var policy = new ProcessPolicy([process.ProcessName]);
        var captured = new InputTarget(123, -1, 0);
        var captures = 0;

        var error = Assert.Throws<InvalidOperationException>(() =>
            GuardedInput.ForegroundTarget(
                policy,
                () =>
                {
                    captures++;
                    return captured; // Focus changed from the allowed process before capture.
                }
            )
        );

        Assert.Equal(1, captures);
        Assert.Contains("not in the FlaUI-MCP app allowlist", error.Message);
        Assert.Contains("Focus an allowed window first", error.Message);
    }

    [Fact]
    public void AllowedInputPreservesCapturedWindowAndPopupIdentity()
    {
        using var process = Process.GetCurrentProcess();
        var captured = new InputTarget(123, process.Id, process.StartTime.ToUniversalTime().Ticks, 456);

        var result = GuardedInput.ForegroundTarget(new ProcessPolicy([process.ProcessName]), () => captured);

        Assert.Same(captured, result);
    }

    [Fact]
    public void ActivationFailureDistinguishesRefusedActivationFromMissingDesktop()
    {
        var wrongWindow = GuardedInput.DescribeActivationFailure(42, 100, 42, "TargetApp")!;
        Assert.StartsWith("activation-denied:", wrongWindow);
        Assert.Contains("another window", wrongWindow);

        var denied = GuardedInput.DescribeActivationFailure(42, 100, 7, "WindowsTerminal")!;
        Assert.StartsWith("activation-denied:", denied);
        Assert.Contains("'WindowsTerminal' (PID 7)", denied);
        Assert.Contains("title bar", denied);

        Assert.StartsWith("desktop-unavailable:", GuardedInput.DescribeActivationFailure(42, 0, 0, null));
    }

    [Fact]
    public void ScreenshotDeniesSelectedProcessBeforeReadingPixels()
    {
        using var process = Process.GetCurrentProcess();
        var capturedPixels = false;

        var error = Assert.Throws<UnauthorizedAccessException>(() =>
            ScreenshotTool.CaptureForeground(-1, new ProcessPolicy([process.ProcessName]), () => capturedPixels = true)
        );

        Assert.False(capturedPixels);
        Assert.Contains("The foreground window's process 'unknown'", error.Message);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ScreenshotCapturesAllowedSelectedProcessOnce(bool restricted)
    {
        using var process = Process.GetCurrentProcess();
        var policy = restricted ? new ProcessPolicy([process.ProcessName]) : ProcessPolicy.AllowAll;
        var captures = 0;

        var image = ScreenshotTool.CaptureForeground(
            process.Id,
            policy,
            () =>
            {
                captures++;
                return "selected-window-pixels";
            }
        );

        Assert.Equal("selected-window-pixels", image);
        Assert.Equal(1, captures);
    }
}
