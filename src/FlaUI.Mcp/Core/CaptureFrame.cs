using System.Drawing;
using System.Drawing.Imaging;
using System.Text.Json.Serialization;

namespace FlaUI.Mcp.Core;

/// <summary>Exact physical-pixel crop; never resizes a screenshot to hide geometry drift.</summary>
public sealed record CaptureFrame(
    [property: JsonPropertyName("sourceWidth")] int? SourceWidth,
    [property: JsonPropertyName("sourceHeight")] int? SourceHeight,
    [property: JsonPropertyName("x"), JsonRequired] int X,
    [property: JsonPropertyName("y"), JsonRequired] int Y,
    [property: JsonPropertyName("width"), JsonRequired] int Width,
    [property: JsonPropertyName("height"), JsonRequired] int Height
)
{
    public void Validate()
    {
        if (
            SourceWidth.HasValue != SourceHeight.HasValue
            || SourceWidth is < 1 or > 8192
            || SourceHeight is < 1 or > 8192
            || X < 0
            || Y < 0
            || Width < 1
            || Height < 1
            || (long)X + Width > SourceWidth
            || (long)Y + Height > SourceHeight
        )
        {
            throw new ArgumentException(
                "Capture frame must be a positive pixel crop contained in the expected source dimensions (maximum 8192 each)"
            );
        }
    }

    public byte[] Apply(byte[] png)
    {
        Validate();
        using var input = new MemoryStream(png);
        using var source = new Bitmap(input);
        if (SourceWidth.HasValue && (source.Width != SourceWidth || source.Height != SourceHeight))
        {
            throw new InvalidOperationException(
                $"Capture geometry changed: expected {SourceWidth}x{SourceHeight}, observed {source.Width}x{source.Height}; review framing, no rescaling performed"
            );
        }

        if ((long)X + Width > source.Width || (long)Y + Height > source.Height)
        {
            throw new ArgumentException("Capture frame is outside the observed image bounds");
        }

        using var crop = source.Clone(new Rectangle(X, Y, Width, Height), PixelFormat.Format32bppArgb);
        crop.SetResolution(96, 96);
        using var output = new MemoryStream();
        crop.Save(output, ImageFormat.Png);
        return output.ToArray();
    }
}
