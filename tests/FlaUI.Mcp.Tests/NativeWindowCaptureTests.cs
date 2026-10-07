using System.Drawing;
using FlaUI.Mcp.Core;
using Xunit;

namespace FlaUI.Mcp.Tests;

public class NativeWindowCaptureTests
{
    [Fact]
    public void TryCaptureHwnd_RejectsMissingHandleWithoutUiAutomation()
    {
        Assert.False(NativeWindowCapture.TryCaptureHwnd(0, out var image, out var reason));
        Assert.Empty(image);
        Assert.Equal("No native window handle available", reason);
    }

    [Fact]
    public void IsBlankOrNearlyBlank_ReturnsTrue_ForBlackBitmap()
    {
        using var bitmap = new Bitmap(16, 16);
        using var graphics = Graphics.FromImage(bitmap);
        graphics.Clear(Color.Black);

        Assert.True(NativeWindowCapture.IsBlankOrNearlyBlank(bitmap));
    }

    [Fact]
    public void IsBlankOrNearlyBlank_ReturnsFalse_ForVisibleContent()
    {
        using var bitmap = new Bitmap(16, 16);
        using var graphics = Graphics.FromImage(bitmap);
        graphics.Clear(Color.Black);
        bitmap.SetPixel(8, 8, Color.White);

        Assert.False(NativeWindowCapture.IsBlankOrNearlyBlank(bitmap));
    }
}
