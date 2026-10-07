using System.Drawing;
using FlaUI.Core.Definitions;
using FlaUI.Mcp.Tools;

namespace FlaUI.Mcp.IntegrationTests;

[Collection("TestApps")]
public sealed class WindowRefCaptureTests(TestAppFixture fixture)
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task StrictNativeHandleAndWindowRefUseWholeWindowBounds(bool useRef)
    {
        var window = fixture.GetWinFormsWindow()!;
        var reference = fixture.Elements.Register(fixture.WinFormsHandle, window);
        var bounds = FlaUI
            .Mcp.Core.Win32Desktop.GetWindowBounds(fixture.Session.GetWindowHwnd(fixture.WinFormsHandle))!
            .Value;
        var result = await new ScreenshotTool(fixture.Session, fixture.Elements).ExecuteAsync(
            System.Text.Json.JsonSerializer.SerializeToElement(
                new
                {
                    handle = useRef ? null : fixture.WinFormsHandle,
                    @ref = useRef ? reference : null,
                    strictNative = true,
                    includeMetadata = true,
                }
            )
        );
        Assert.NotEqual(true, result.IsError);
        var png = result.Content.Single(c => c.Type == "image");
        using var stream = new MemoryStream(Convert.FromBase64String(png.Data!));
        using var image = Image.FromStream(stream);
        Assert.Equal(bounds.Size, image.Size);
        using var metadata = System.Text.Json.JsonDocument.Parse(result.Content.Last(c => c.Type == "text").Text!);
        Assert.Equal("native-window", metadata.RootElement.GetProperty("method").GetString());
        Assert.False(metadata.RootElement.GetProperty("screenFallback").GetBoolean());
    }

    [Fact]
    public async Task DefaultCaptureStillUsesUiaBounds()
    {
        var result = await new ScreenshotTool(fixture.Session, fixture.Elements).ExecuteAsync(
            System.Text.Json.JsonSerializer.SerializeToElement(
                new { handle = fixture.WinFormsHandle, includeMetadata = true }
            )
        );
        Assert.NotEqual(true, result.IsError);
        using var metadata = System.Text.Json.JsonDocument.Parse(result.Content.Last(c => c.Type == "text").Text!);
        Assert.Equal("screen-uia-bounds", metadata.RootElement.GetProperty("method").GetString());
    }

    [Fact]
    public async Task NativeCaptureMetadataDescribesTheReturnedImage()
    {
        var reference = fixture.Elements.Register(fixture.WinFormsHandle, fixture.GetWinFormsWindow()!);
        var result = await new ScreenshotTool(fixture.Session, fixture.Elements).ExecuteAsync(
            System.Text.Json.JsonSerializer.SerializeToElement(
                new
                {
                    @ref = reference,
                    background = true,
                    includeMetadata = true,
                }
            )
        );
        Assert.NotEqual(true, result.IsError);
        var png = result.Content.Single(c => c.Type == "image");
        using var stream = new MemoryStream(Convert.FromBase64String(png.Data!));
        using var image = Image.FromStream(stream);
        using var metadata = System.Text.Json.JsonDocument.Parse(result.Content.Last(c => c.Type == "text").Text!);
        Assert.Equal("native-window", metadata.RootElement.GetProperty("method").GetString());
        Assert.Equal(image.Width, metadata.RootElement.GetProperty("width").GetInt32());
        Assert.Equal(image.Height, metadata.RootElement.GetProperty("height").GetInt32());
        Assert.True(metadata.RootElement.GetProperty("windowDpi").GetUInt32() > 0);
    }

    [Fact]
    public async Task NativeWindowRefProducesDecodablePng()
    {
        var window = fixture.GetWinFormsWindow()!;
        var reference = fixture.Elements.Register(fixture.WinFormsHandle, window);
        var path = Path.Combine(Path.GetTempPath(), $"fla_capture_{Guid.NewGuid():N}.png");
        try
        {
            var result = await fixture.CallTool(
                new ScreenshotTool(fixture.Session, fixture.Elements),
                new
                {
                    @ref = reference,
                    background = true,
                    savePath = path,
                    includeImage = false,
                }
            );
            Assert.True(File.Exists(path), result);
            using var image = Image.FromFile(path);
            Assert.True(image.Width > 100 && image.Height > 100);
        }
        finally
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
    }

    [Fact]
    public async Task NativeRefRejectsNonWindowWithoutCreatingImage()
    {
        var button = fixture.GetWinFormsWindow()!.FindFirstDescendant(cf => cf.ByControlType(ControlType.Button));
        Assert.NotNull(button);
        var reference = fixture.Elements.Register(fixture.WinFormsHandle, button);
        var path = Path.Combine(Path.GetTempPath(), $"fla_capture_{Guid.NewGuid():N}.png");
        try
        {
            var result = await fixture.CallTool(
                new ScreenshotTool(fixture.Session, fixture.Elements),
                new
                {
                    @ref = reference,
                    background = true,
                    savePath = path,
                    includeImage = false,
                }
            );
            Assert.Contains("requires a Window element", result);
            Assert.False(File.Exists(path));
        }
        finally
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
    }
}
