using FlaUI.Mcp.Core;
using Xunit;

namespace FlaUI.Mcp.Tests;

/// <summary>
/// A window laying out for one DPI while sitting on a monitor at another invalidates the geometry
/// every capture path assumes. Observed in the field: a native capture came back oversized by
/// exactly the ratio while reporting the expected bitmap size.
/// </summary>
public sealed class DpiMismatchTests
{
    [Fact]
    public void AgreementIsNotReported()
    {
        Assert.Null(DpiUtility.DescribeMismatch(168, 168));
    }

    [Theory]
    [InlineData(null, 168u)]
    [InlineData(168u, null)]
    [InlineData(null, null)]
    public void UnknownDpiIsNotAMismatch(uint? window, uint? monitor)
    {
        // Absence of a reading is not evidence of disagreement.
        Assert.Null(DpiUtility.DescribeMismatch(window, monitor));
    }

    [Fact]
    public void MismatchNamesBothValuesAndTheRatio()
    {
        // The observed case: window still at 175% after the display moved to 125%.
        var warning = DpiUtility.DescribeMismatch(168, 120);
        Assert.NotNull(warning);
        Assert.Contains("168", warning);
        Assert.Contains("120", warning);
        Assert.Contains("1.4", warning);
    }

    [Fact]
    public void MismatchDoesNotPromiseThatRestartingFixesIt()
    {
        // A DPI-unaware process reports 96 forever on a scaled monitor and its screen pixels are
        // correct; telling the caller to restart would be false advice for that whole class.
        var warning = DpiUtility.DescribeMismatch(96, 168);
        Assert.NotNull(warning);
        Assert.DoesNotContain("restart", warning, System.StringComparison.OrdinalIgnoreCase);
        Assert.Contains("DPI-unaware", warning);
    }

    [Fact]
    public void MismatchIsDirectionAgnostic()
    {
        Assert.NotNull(DpiUtility.DescribeMismatch(120, 168));
        Assert.NotNull(DpiUtility.DescribeMismatch(168, 120));
    }
}
