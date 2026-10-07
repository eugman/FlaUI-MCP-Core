using System.Drawing;
using System.Drawing.Imaging;
using System.Text.Json;
using FlaUI.Mcp.Core;
using FlaUI.Mcp.Tools;
using Xunit;

namespace FlaUI.Mcp.Tests;

public sealed class CaptureFrameTests
{
    [Fact]
    public void CancelledCaptureCannotOverwriteExistingArtifact()
    {
        var path = Path.Combine(Path.GetTempPath(), "fla_cancelled_" + Guid.NewGuid().ToString("N") + ".png");
        byte[] original = [1, 2, 3];
        File.WriteAllBytes(path, original);
        var operation = new OperationContext();
        operation.Stop.Cancel();
        OperationContext.Current.Value = operation;
        try
        {
            Assert.Throws<OperationCanceledException>(() =>
                ScreenshotTool.BuildScreenshotResult([4, 5], path, true, false)
            );
            Assert.Equal(original, File.ReadAllBytes(path));
        }
        finally
        {
            OperationContext.Current.Value = null;
            File.Delete(path);
        }
    }

    [Theory]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task StrictCaptureRequiresExplicitWindowBeforeDesktopAccess(bool restricted, bool strict)
    {
        var tool = new ScreenshotTool(
            null!,
            new ElementRegistry(),
            processPolicy: restricted ? new ProcessPolicy(["te"]) : ProcessPolicy.AllowAll
        );
        var result = await tool.ExecuteAsync(JsonSerializer.SerializeToElement(new { strictNative = strict }));
        Assert.True(result.IsError);
        Assert.Contains("requires a window handle or Window ref", result.Content[0].Text);
    }

    [Fact]
    public void GeometryFailureDoesNotCreateArtifactDirectory()
    {
        var root = Path.Combine(Path.GetTempPath(), "fla_bad_frame_" + Guid.NewGuid().ToString("N"));
        try
        {
            Assert.Throws<InvalidOperationException>(() =>
                ScreenshotTool.BuildScreenshotResult(
                    Pixels(),
                    Path.Combine(root, "frame.png"),
                    false,
                    false,
                    new CaptureFrame(9, 6, 0, 0, 8, 6)
                )
            );
            Assert.False(Directory.Exists(root));
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, true);
            }
        }
    }

    [Fact]
    public void UnframedCaptureRetainsOriginalBytes()
    {
        var bytes = Pixels(120);
        var result = ScreenshotTool.BuildScreenshotResult(bytes, null, false, true);
        Assert.Equal(bytes, Convert.FromBase64String(Assert.Single(result.Content).Data!));
    }

    private static byte[] Pixels(float dpi = 96)
    {
        using var bitmap = new Bitmap(8, 6, PixelFormat.Format32bppArgb);
        bitmap.SetResolution(dpi, dpi);
        for (var y = 0; y < bitmap.Height; y++)
        {
            for (var x = 0; x < bitmap.Width; x++)
            {
                bitmap.SetPixel(x, y, Color.FromArgb(255, x * 20, y * 30, x + y));
            }
        }

        using var stream = new MemoryStream();
        bitmap.Save(stream, ImageFormat.Png);
        return stream.ToArray();
    }

    [Fact]
    public void CropPreservesEverySelectedPixelAndDoesNotResample()
    {
        var frame = new CaptureFrame(8, 6, 2, 1, 4, 3);
        using var stream = new MemoryStream(frame.Apply(Pixels()));
        using var result = new Bitmap(stream);
        Assert.Equal(4, result.Width);
        Assert.Equal(3, result.Height);
        for (var y = 0; y < result.Height; y++)
        {
            for (var x = 0; x < result.Width; x++)
            {
                Assert.Equal(
                    Color.FromArgb(255, (x + 2) * 20, (y + 1) * 30, x + y + 3).ToArgb(),
                    result.GetPixel(x, y).ToArgb()
                );
            }
        }
    }

    [Fact]
    public void EncodingDoesNotDependOnInputDpiMetadata()
    {
        var frame = new CaptureFrame(8, 6, 0, 0, 8, 6);
        Assert.Equal(frame.Apply(Pixels(96)), frame.Apply(Pixels(120)));
    }

    [Fact]
    public void ChangedSourceDimensionsFailInsteadOfResizing() =>
        Assert.Throws<InvalidOperationException>(() => new CaptureFrame(9, 6, 0, 0, 8, 6).Apply(Pixels()));

    [Theory]
    [InlineData(-1, 0, 2, 2)]
    [InlineData(0, -1, 2, 2)]
    [InlineData(7, 0, 2, 2)]
    [InlineData(0, 5, 2, 2)]
    [InlineData(0, 0, 0, 2)]
    [InlineData(int.MaxValue, 0, int.MaxValue, 2)]
    public void InvalidAndOverflowingCropsFail(int x, int y, int width, int height) =>
        Assert.Throws<ArgumentException>(() => new CaptureFrame(8, 6, x, y, width, height).Validate());

    [Fact]
    public void AllWireCoordinatesAreExplicitAndCamelCase()
    {
        var value = JsonSerializer.SerializeToElement(new CaptureFrame(8, 6, 2, 1, 4, 3));
        Assert.Equal(8, value.GetProperty("sourceWidth").GetInt32());
        Assert.Equal(6, value.EnumerateObject().Count());
        Assert.Throws<JsonException>(() =>
            JsonSerializer.Deserialize<CaptureFrame>("{\"sourceWidth\":8,\"sourceHeight\":6,\"width\":4,\"height\":3}")
        );
    }

    [Fact]
    public void SourceSizeMayBeOmittedButNotHalfSpecified()
    {
        var frame = JsonSerializer.Deserialize<CaptureFrame>("{\"x\":2,\"y\":1,\"width\":4,\"height\":3}")!;
        using var stream = new MemoryStream(frame.Apply(Pixels()));
        using var image = new Bitmap(stream);
        Assert.Equal(new Size(4, 3), image.Size);
        Assert.Throws<ArgumentException>(() => new CaptureFrame(8, null, 0, 0, 4, 3).Validate());
    }
}
