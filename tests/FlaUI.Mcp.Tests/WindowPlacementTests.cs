using System.Drawing;
using FlaUI.Mcp.Core;
using FlaUI.Mcp.Tools;
using Xunit;

namespace FlaUI.Mcp.Tests;

public sealed class WindowPlacementTests
{
    [Fact]
    public async Task HandlePlacementWithoutSessionsReturnsClearError()
    {
        var tool = new WindowPlacementTool(new ElementRegistry(), new PendingInvokeTracker());
        var result = await tool.ExecuteAsync(
            System.Text.Json.JsonSerializer.SerializeToElement(
                new { handle = "w1", placement = new WindowPlacement(0, 0, 700, 400) }
            )
        );
        Assert.True(result.IsError);
        Assert.Contains("requires a session manager", result.Content[0].Text);
    }

    [Fact]
    public async Task MissingTargetIsRejectedBeforeWindowAccess()
    {
        var tool = new WindowPlacementTool(new ElementRegistry(), new PendingInvokeTracker());
        var result = await tool.ExecuteAsync(
            System.Text.Json.JsonSerializer.SerializeToElement(new { placement = new WindowPlacement(0, 0, 700, 400) })
        );
        Assert.True(result.IsError);
        Assert.Contains("ref or handle required", result.Content[0].Text);
    }

    [Fact]
    public void MissingCoordinatesCannotImplicitlyMoveWindowToOrigin() =>
        Assert.Throws<System.Text.Json.JsonException>(() =>
            System.Text.Json.JsonSerializer.Deserialize<WindowPlacement>("{\"width\":700,\"height\":400}")
        );

    [Fact]
    public void DefaultSerializerMatchesToolWireContract()
    {
        var args = System.Text.Json.JsonSerializer.SerializeToElement(
            new { placement = new WindowPlacement(-50, 20, 700, 400) }
        );
        var placement = args.GetProperty("placement");
        Assert.Equal(-50, placement.GetProperty("x").GetInt32());
        Assert.Equal(20, placement.GetProperty("y").GetInt32());
        Assert.Equal(700, placement.GetProperty("width").GetInt32());
        Assert.Equal(400, placement.GetProperty("height").GetInt32());
        Assert.Equal(4, placement.EnumerateObject().Count());
    }

    [Fact]
    public void SupportsNegativeCoordinateMonitorWithoutRescaling()
    {
        var placement = new WindowPlacement(-1800, 40, 700, 400);
        placement.RequireVisible([new(-1920, 0, 1920, 1040), new(0, 0, 1920, 1040)]);
    }

    [Theory]
    [InlineData(-50, 0, 700, 400)]
    [InlineData(0, 800, 700, 400)]
    [InlineData(4000, 0, 700, 400)]
    public void RejectsSpanningClippedAndUnavailableMonitor(int x, int y, int width, int height) =>
        Assert.Throws<InvalidOperationException>(() =>
            new WindowPlacement(x, y, width, height).RequireVisible([new(-1920, 0, 1920, 1040), new(0, 0, 1920, 1040)])
        );

    [Theory]
    [InlineData(0, 0, 0, 400)]
    [InlineData(0, 0, 9000, 400)]
    [InlineData(int.MaxValue, 0, 700, 400)]
    [InlineData(0, int.MinValue, 700, 400)]
    public void RejectsInvalidGeometryBeforeNativeAccess(int x, int y, int width, int height) =>
        Assert.Throws<ArgumentException>(() => new WindowPlacement(x, y, width, height).Validate());
}
