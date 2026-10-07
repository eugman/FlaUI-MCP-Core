using System.Drawing;
using System.Text.Json;
using FlaUI.Mcp.Core;
using FlaUI.Mcp.Tools;
using Xunit;

namespace FlaUI.Mcp.Tests;

/// <summary>
/// Annotation coordinates must come from the same observation as the pixels. Converting
/// windows_find bounds by hand put callout boxes on the wrong control twice, and the
/// title-bar offset that conversion needs is not even constant across DPI states (30).
/// </summary>
public sealed class LocateTests
{
    // A window capture whose pixels start at screen (960, 39).
    private static readonly Rectangle Source = new(960, 39, 958, 1100);

    [Fact]
    public void ScreenBoundsAreTranslatedToImagePixels()
    {
        var located = ScreenshotTool.LocateInCapture(
            "w1e5",
            new Rectangle(1000, 139, 120, 26),
            Source,
            958,
            1100,
            null
        );
        Assert.Equal(40, located.X);
        Assert.Equal(100, located.Y);
        Assert.Equal(120, located.Width);
        Assert.Equal(26, located.Height);
        Assert.False(located.Clipped);
    }

    [Fact]
    public void TheCaptureOriginItselfMapsToZeroZero()
    {
        var located = ScreenshotTool.LocateInCapture("w1e5", new Rectangle(960, 39, 10, 10), Source, 958, 1100, null);
        Assert.Equal(0, located.X);
        Assert.Equal(0, located.Y);
    }

    [Fact]
    public void ACropShiftsCoordinatesAgain()
    {
        var frame = new CaptureFrame(null, null, 20, 50, 400, 300);
        var located = ScreenshotTool.LocateInCapture(
            "w1e5",
            new Rectangle(1000, 139, 120, 26),
            Source,
            958,
            1100,
            frame
        );
        // 40 - 20 and 100 - 50.
        Assert.Equal(20, located.X);
        Assert.Equal(50, located.Y);
        Assert.False(located.Clipped);
    }

    [Fact]
    public void AnElementOutsideTheCropIsReportedNotClamped()
    {
        var frame = new CaptureFrame(null, null, 0, 0, 100, 100);
        var located = ScreenshotTool.LocateInCapture(
            "w1e5",
            new Rectangle(1500, 800, 50, 20),
            Source,
            958,
            1100,
            frame
        );
        Assert.True(located.Clipped);
        // Clamping to the edge would look plausible and be wrong; the real offset survives.
        Assert.Equal(540, located.X);
        Assert.Equal(761, located.Y);
    }

    [Fact]
    public void AnElementStartingLeftOfTheCaptureGetsNegativeCoordinates()
    {
        var located = ScreenshotTool.LocateInCapture("w1e5", new Rectangle(900, 20, 100, 40), Source, 958, 1100, null);
        Assert.Equal(-60, located.X);
        Assert.Equal(-19, located.Y);
        Assert.True(located.Clipped);
    }

    [Fact]
    public void PartialOverlapCountsAsClipped()
    {
        // Straddles the right edge of the image: usable, but the caller must know it is cut.
        var located = ScreenshotTool.LocateInCapture("w1e5", new Rectangle(1900, 100, 50, 20), Source, 958, 1100, null);
        Assert.True(located.Clipped);
    }

    [Fact]
    public void AnElementFillingTheImageExactlyIsNotClipped()
    {
        var located = ScreenshotTool.LocateInCapture(
            "w1e5",
            new Rectangle(960, 39, 958, 1100),
            Source,
            958,
            1100,
            null
        );
        Assert.Equal(0, located.X);
        Assert.False(located.Clipped);
    }

    [Fact]
    public void TheRefIsCarriedThroughSoResultsCanBeMatchedUp()
    {
        var located = ScreenshotTool.LocateInCapture(
            "w2e267",
            new Rectangle(1000, 139, 10, 10),
            Source,
            958,
            1100,
            null
        );
        Assert.Equal("w2e267", located.Ref);
    }

    [Fact]
    public void AnOffsetOfThirtyNineIsNotBakedIn()
    {
        // The observed title-bar offset was 39px in one DPI state and 55px in another on the same
        // window, which is why the offset comes from sourceBounds rather than a constant.
        var other = new Rectangle(960, 55, 958, 1084);
        var located = ScreenshotTool.LocateInCapture("w1e5", new Rectangle(1000, 155, 10, 10), other, 958, 1084, null);
        Assert.Equal(40, located.X);
        Assert.Equal(100, located.Y);
    }

    [Fact]
    public async Task LocateOnFullScreenIsRefusedBeforeAnythingIsCaptured()
    {
        // fullScreen records no origin, so there is nothing to translate against. Refusing up front
        // means a malformed call does not grab and encode a PNG only to throw it away.
        var tool = new ScreenshotTool(new SessionManager(null, ProcessPolicy.AllowAll), new ElementRegistry());
        var result = await tool.ExecuteAsync(
            JsonSerializer.SerializeToElement(new { fullScreen = true, locate = new[] { "w1e5" } })
        );
        Assert.True(result.IsError);
        Assert.Contains("No screenshot was taken", result.Content[0].Text);
    }

    [Fact]
    public async Task LocateRejectsAnUnknownRefBeforeCapturing()
    {
        var tool = new ScreenshotTool(new SessionManager(null, ProcessPolicy.AllowAll), new ElementRegistry());
        var result = await tool.ExecuteAsync(
            JsonSerializer.SerializeToElement(new { handle = "w1", locate = new[] { "nope" } })
        );
        Assert.True(result.IsError);
        Assert.Contains("not a known element ref", result.Content[0].Text);
        Assert.Contains("No screenshot was taken", result.Content[0].Text);
    }
}
