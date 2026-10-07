using System.Drawing;
using FlaUI.Mcp.Core;
using FlaUI.Mcp.Tools;
using Xunit;

namespace FlaUI.Mcp.Tests;

/// <summary>
/// The choices that decide whether a capture is trustworthy. They used to live inside the capture
/// path, so CI could only reach the message formatters — the parts least likely to be wrong.
/// </summary>
public sealed class CaptureVerdictTests
{
    private static Win32Desktop.OcclusionCheck Covered() =>
        new(TargetFound: true, [new Win32WindowInfo(42, "Windows Terminal", 23296, true, false, false)]);

    private static Win32Desktop.OcclusionCheck Clean() => new(TargetFound: true, []);

    private static ScreenshotTool.CaptureVerdict Decide(
        string method = "screen-uia-bounds",
        bool exempt = false,
        Win32Desktop.OcclusionCheck? occlusion = null,
        string? dpi = null,
        bool allow = false
    ) => ScreenshotTool.Decide(method, exempt, occlusion, dpi, allow);

    [Fact]
    public void ACleanCaptureProceedsSilently()
    {
        var verdict = Decide(occlusion: Clean());
        Assert.Null(verdict.Refusal);
        Assert.Empty(verdict.Warnings);
    }

    [Fact]
    public void AnOccludedScreenCaptureIsRefusedAndNamesTheObstruction()
    {
        var verdict = Decide(occlusion: Covered());
        Assert.NotNull(verdict.Refusal);
        Assert.Contains("Windows Terminal", verdict.Refusal);
        Assert.Contains("23296", verdict.Refusal);
        Assert.Contains("strictNative", verdict.Refusal);
    }

    [Fact]
    public void ADpiMismatchWarnsAndNeverRefuses()
    {
        // The deliberate decision: a screen-pixel capture under DPI mismatch returns what is on the
        // glass, which for photography is correct, and it is permanent for DPI-unaware apps.
        var verdict = Decide(occlusion: Clean(), dpi: "Window DPI 168 does not match monitor DPI 120");
        Assert.Null(verdict.Refusal);
        Assert.Contains(verdict.Warnings, w => w.Contains("168") && w.Contains("120"));
    }

    [Fact]
    public void ADpiMismatchIsStillReportedAlongsideAnOcclusionRefusal()
    {
        // A refusal must not swallow an unrelated finding.
        var verdict = Decide(occlusion: Covered(), dpi: "Window DPI 168 does not match monitor DPI 120");
        Assert.NotNull(verdict.Refusal);
        Assert.Contains(verdict.Warnings, w => w.Contains("168"));
    }

    [Fact]
    public void NativeCaptureIsNotRefusedForOcclusion()
    {
        // PrintWindow renders the window's own surface, so what is in front of it is irrelevant.
        var verdict = Decide(method: "native-window", occlusion: Covered());
        Assert.Null(verdict.Refusal);
    }

    [Fact]
    public void TheBlockedProviderFallbackWarnsInsteadOfRefusing()
    {
        // That path exists because a modal is up — the modal is in front by construction, and
        // strictNative is already refused there, so refusing would leave no way to capture at all.
        var verdict = Decide(method: "screen-fallback", exempt: true, occlusion: Covered());
        Assert.Null(verdict.Refusal);
        Assert.Contains(verdict.Warnings, w => w.Contains("Windows Terminal"));
    }

    [Fact]
    public void AnUnexemptScreenFallbackIsStillRefused()
    {
        // The other two screen-fallback origins — a failed native capture and a UIA timeout — have
        // no modal by construction, so they get no exemption.
        Assert.NotNull(Decide(method: "screen-fallback", exempt: false, occlusion: Covered()).Refusal);
    }

    [Fact]
    public void AllowOccludedPixelsReturnsTheImageAndStillStatesTheFinding()
    {
        var verdict = Decide(occlusion: Covered(), allow: true);
        Assert.Null(verdict.Refusal);
        Assert.Contains(verdict.Warnings, w => w.Contains("Windows Terminal"));
        // Saying nothing was returned would be false here; that wording belongs only to a refusal.
        Assert.DoesNotContain(verdict.Warnings, w => w.Contains("No screenshot was returned"));
    }

    [Fact]
    public void AnUncheckedCaptureIsNotTreatedAsClean()
    {
        // Null means the capture could not be checked. It must not refuse, and must not claim clean.
        var verdict = Decide(occlusion: null);
        Assert.Null(verdict.Refusal);
        Assert.Null(ScreenshotTool.DescribeOcclusion(null));
    }

    [Fact]
    public void ATargetMissingFromTheZOrderIsRefusedAsUnproven()
    {
        var missing = new Win32Desktop.OcclusionCheck(TargetFound: false, []);
        var verdict = Decide(occlusion: missing);
        Assert.NotNull(verdict.Refusal);
        Assert.Contains("not found in the desktop window order", verdict.Refusal);
    }

    [Fact]
    public void EveryObstructionIsNamed()
    {
        var two = new Win32Desktop.OcclusionCheck(
            true,
            [
                new Win32WindowInfo(1, "First", 10, true, false, false),
                new Win32WindowInfo(2, "", 20, true, false, false),
            ]
        );
        var finding = ScreenshotTool.DescribeOcclusion(two)!;
        Assert.Contains("First", finding);
        Assert.Contains("untitled", finding);
    }

    [Theory]
    [InlineData("screen-uia-bounds", true)]
    [InlineData("screen-fallback", true)]
    [InlineData("native-window", false)]
    [InlineData("screen", false)]
    public void OnlyScreenPixelMethodsCanBeOccluded(string method, bool reads) =>
        Assert.Equal(reads, ScreenshotTool.ReadsScreenPixels(method));

    [Fact]
    public void AScreenCaptureWithNoKnownTargetIsReportedAsUnchecked()
    {
        // The guard was skipped whenever the target window was unknown, and the reply then looked
        // exactly like a verified clean capture. Unchecked is not clean.
        var reason = ScreenshotTool.UncheckedReason("screen-uia-bounds", 0, new System.Drawing.Rectangle(0, 0, 10, 10));
        Assert.NotNull(reason);
        Assert.Contains("not checked", reason);
    }

    [Fact]
    public void AnUncheckedReasonSurfacesAsAWarningWithoutRefusing()
    {
        // It has to reach the caller on a default call, not only behind includeMetadata.
        var verdict = ScreenshotTool.Decide(
            "screen-uia-bounds",
            occlusionExempt: false,
            occlusion: null,
            dpiWarning: null,
            allowOccludedPixels: false,
            uncheckedReason: "unchecked because reasons"
        );
        Assert.Null(verdict.Refusal);
        Assert.Contains("unchecked because reasons", verdict.Warnings);
    }

    [Fact]
    public void NativeCaptureNeedsNoObstructionCheck()
    {
        // It renders the window's own surface, so nothing layered over it is in the image.
        Assert.Null(ScreenshotTool.UncheckedReason("native-hwnd", 0, null));
    }

    [Fact]
    public void AKnownTargetAndRectangleHaveNothingToExcuse() =>
        Assert.Null(
            ScreenshotTool.UncheckedReason("screen-uia-bounds", 42, new System.Drawing.Rectangle(0, 0, 10, 10))
        );
}
