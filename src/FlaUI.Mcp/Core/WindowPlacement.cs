using System.Drawing;
using System.Text.Json.Serialization;

namespace FlaUI.Mcp.Core;

/// <summary>Physical-pixel placement. Never clamp, rescale, or retry a window mutation.</summary>
public sealed record WindowPlacement(
    [property: JsonPropertyName("x"), JsonRequired] int X,
    [property: JsonPropertyName("y"), JsonRequired] int Y,
    [property: JsonPropertyName("width"), JsonRequired] int Width,
    [property: JsonPropertyName("height"), JsonRequired] int Height
)
{
    [JsonIgnore]
    public Rectangle Bounds => new(X, Y, Width, Height);

    public void Validate()
    {
        if (
            Width is < 100 or > 8192
            || Height is < 100 or > 8192
            || X is < -100000 or > 100000
            || Y is < -100000 or > 100000
        )
        {
            throw new ArgumentException(
                "Window placement requires dimensions 100..8192 and coordinates -100000..100000 physical pixels"
            );
        }
    }

    public void RequireVisible(IEnumerable<Rectangle> workAreas)
    {
        Validate();
        if (!workAreas.Any(area => area.Contains(Bounds)))
        {
            throw new InvalidOperationException(
                "Requested window must fit entirely in one monitor work area; no clipping or automatic resizing"
            );
        }
    }
}
