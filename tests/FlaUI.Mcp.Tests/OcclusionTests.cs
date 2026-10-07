using System.Drawing;
using FlaUI.Mcp.Core;
using Xunit;

namespace FlaUI.Mcp.Tests;

/// <summary>
/// Screen-pixel capture reads the glass, so a window layered over the target lands in the image
/// while the call still reports success. The decision is a pure function over an
/// injected observation, which is the only reason it is testable without a desktop.
/// </summary>
public sealed class OcclusionTests
{
    private const int TargetPid = 100;
    private static readonly Rectangle Target = new(960, 0, 960, 1140);

    private static Win32Desktop.WindowBounds Window(
        nint hwnd,
        int processId,
        Rectangle? bounds,
        bool cloaked = false,
        string title = "w",
        bool canCover = true
    ) =>
        new(
            new Win32WindowInfo(hwnd, title, processId, IsEnabled: true, IsToolWindow: false, IsCloaked: cloaked),
            bounds,
            canCover
        );

    private static Win32Desktop.WindowBounds TargetWindow() => Window(1, TargetPid, Target, title: "Target");

    private static Win32Desktop.OcclusionCheck Check(params Win32Desktop.WindowBounds[] zOrder) =>
        Win32Desktop.FindObstructions(zOrder, target: 1, TargetPid, Target);

    [Fact]
    public void NothingAboveTheTargetIsClean() =>
        Assert.True(Check(TargetWindow(), Window(2, 200, new Rectangle(0, 0, 100, 100))).IsClean);

    [Fact]
    public void AWindowAboveThatMissesTheRectIsNotAnObstruction() =>
        Assert.True(Check(Window(2, 200, new Rectangle(0, 0, 500, 500)), TargetWindow()).IsClean);

    [Fact]
    public void APartialOverlapIsCaught()
    {
        // The observed case: a terminal covering a 60px strip of the target's left edge. Point
        // sampling at the corners and centre can miss a band like this; intersection cannot.
        var terminal = Window(2, 23296, new Rectangle(44, 0, 976, 1148), title: "Windows Terminal");
        var check = Check(terminal, TargetWindow());
        Assert.False(check.IsClean);
        Assert.Equal("Windows Terminal", Assert.Single(check.Obstructions).Title);
    }

    [Fact]
    public void WindowsBelowTheTargetAreIgnored() =>
        Assert.True(Check(TargetWindow(), Window(3, 200, Target, title: "Behind")).IsClean);

    [Fact]
    public void SameProcessWindowsAreAllowed() =>
        // An owned dialog or menu of the app being captured is usually the subject, not an intruder.
        Assert.True(Check(Window(2, TargetPid, Target, title: "Use a Workspace Database?"), TargetWindow()).IsClean);

    [Fact]
    public void CloakedWindowsAreIgnored() =>
        Assert.True(Check(Window(2, 200, Target, cloaked: true, title: "Suspended"), TargetWindow()).IsClean);

    [Fact]
    public void UnreadableBoundsCountAsCovering() =>
        // Absence of a rectangle is not evidence the window is elsewhere.
        Assert.False(Check(Window(2, 200, bounds: null, title: "Unreadable"), TargetWindow()).IsClean);

    [Fact]
    public void AMissingTargetFailsClosedRatherThanReportingClean()
    {
        var check = Check(Window(2, 200, new Rectangle(0, 0, 10, 10)));
        Assert.False(check.TargetFound);
        Assert.False(check.IsClean);
    }

    [Fact]
    public void AnEmptyDesktopFailsClosed() =>
        Assert.False(Win32Desktop.FindObstructions([], 1, TargetPid, Target).IsClean);

    [Fact]
    public void EdgeTouchingWindowsDoNotIntersect() =>
        // Right and bottom edges are exclusive: a window starting exactly where the target ends is
        // adjacent, not overlapping.
        Assert.True(
            Check(Window(2, 200, new Rectangle(1920, 0, 100, 1140), title: "Adjacent"), TargetWindow()).IsClean
        );

    [Fact]
    public void SnappedWindowsDoNotCountAsOverlapping()
    {
        // Observed: Windows snap placed a terminal beside the target app and every capture was refused.
        // ObserveZOrder feeds DWM extended frame bounds, where the two abut exactly.
        var target = new Rectangle(0, 0, 1291, 1140);
        var snapped = Window(2, 200, new Rectangle(1291, 0, 629, 1140), title: "Snapped beside");
        Assert.True(
            Win32Desktop
                .FindObstructions(
                    [snapped, Window(1, TargetPid, target, title: "Target")],
                    target: 1,
                    TargetPid,
                    target
                )
                .IsClean
        );
    }

    [Fact]
    public void TheUnadjustedRectangleWouldHaveBeenAFalsePositive()
    {
        // The same pair as GetWindowRect reports it, which is what the guard used before the fix:
        // 8px of invisible resize border overlapping. This is the input the DWM adjustment avoids,
        // and it must still read as an obstruction so the adjustment stays load-bearing.
        var target = new Rectangle(0, 0, 1291, 1140);
        var raw = Window(2, 200, new Rectangle(1283, 0, 645, 1148), title: "Snapped beside");
        Assert.False(
            Win32Desktop
                .FindObstructions([raw, Window(1, TargetPid, target, title: "Target")], target: 1, TargetPid, target)
                .IsClean
        );
    }

    [Fact]
    public void TinyHelperWindowsAreIgnored() =>
        // The desktop is full of 1x1 shell windows parked at the origin; they cannot meaningfully
        // corrupt a capture and refusing on them would block every screenshot.
        Assert.True(Check(Window(2, 200, new Rectangle(0, 0, 1, 1), title: ""), TargetWindow()).IsClean);

    [Fact]
    public void AWindowJustLargeEnoughStillCounts() =>
        Assert.False(
            Check(
                Window(
                    2,
                    200,
                    new Rectangle(1000, 100, Win32Desktop.MinimumObstructionSize, Win32Desktop.MinimumObstructionSize)
                ),
                TargetWindow()
            ).IsClean
        );

    [Fact]
    public void EveryObstructionIsFoundNotJustTheFirst()
    {
        var check = Check(
            Window(2, 200, Target, title: "First"),
            Window(3, 300, Target, title: "Second"),
            TargetWindow()
        );
        Assert.Equal(["First", "Second"], check.Obstructions.Select(w => w.Title));
    }

    [Fact]
    public void NegativeMonitorCoordinatesAreHandled()
    {
        // A monitor arranged above or left of the primary has negative desktop coordinates.
        var target = new Rectangle(-1920, -1080, 960, 540);
        var over = Window(2, 200, new Rectangle(-1800, -1000, 400, 300), title: "Overlapping");
        Assert.False(
            Win32Desktop
                .FindObstructions([over, Window(1, TargetPid, target, title: "Target")], target: 1, TargetPid, target)
                .IsClean
        );
    }

    [Fact]
    public void AMinimizedWindowIsNotAnObstruction()
    {
        // DWMWA_EXTENDED_FRAME_BOUNDS reports a minimized window's *restored* rectangle, so
        // measuring one yields a full-size overlap for a window that is not on screen at all.
        var minimized = Window(2, 200, Target, title: "Minimized", canCover: false);
        Assert.True(Check(minimized, TargetWindow()).IsClean);
    }

    [Fact]
    public void AClickThroughOverlayIsNotAnObstruction()
    {
        // WS_EX_TRANSPARENT and zero-alpha layered windows sit above the target and show what is
        // behind them, so they are in the z-order but not in the pixels.
        var overlay = Window(2, 200, Target, title: "Overlay", canCover: false);
        Assert.True(Check(overlay, TargetWindow()).IsClean);
    }

    [Fact]
    public void AnEightPixelSharedEdgeIsNotAnObstruction()
    {
        // Windows snap leaves adjacent windows sharing their invisible resize border. The overlap
        // is a few pixels of frame and hides nothing; testing the covering window's own size
        // instead of the overlap reported this as a real obstruction.
        var snapped = Window(2, 200, new Rectangle(Target.Left - 1000, 0, 1004, 1140), title: "Terminal");
        Assert.True(Check(snapped, TargetWindow()).IsClean);
    }

    [Fact]
    public void ALargeWindowOverlappingByAWideBandIsStillCaught()
    {
        // The counterpart: the threshold must not be a loophole for a real overlap.
        var covering = Window(2, 200, new Rectangle(Target.Left - 1000, 0, 1200, 1140), title: "Terminal");
        Assert.False(Check(covering, TargetWindow()).IsClean);
    }
}
