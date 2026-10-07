using FlaUI.Mcp.Core;
using Xunit;

namespace FlaUI.Mcp.Tests;

public class TextSettlementTests
{
    [Fact]
    public void ObservesDelayedUpdateWithoutAnotherDispatch()
    {
        var reads = 0;
        var waits = 0;
        var checks = 0;
        var result = ControlText.WaitForExpected(
            "new",
            () => new(++reads < 3 ? "old" : "new", "Value"),
            () => checks++,
            () => waits++
        );
        Assert.Equal("verified", result.Verify("new"));
        Assert.Equal(3, reads);
        Assert.Equal(3, checks);
        Assert.Equal(2, waits);
    }

    [Fact]
    public void MismatchIsBoundedAndUnavailableStopsImmediately()
    {
        var waits = 0;
        Assert.Equal(
            "mismatch",
            ControlText.WaitForExpected("new", () => new("old", "Value"), () => { }, () => waits++).Verify("new")
        );
        Assert.Equal(10, waits);
        waits = 0;
        Assert.Equal(
            "unavailable",
            ControlText.WaitForExpected("new", () => new(null, "Text"), () => { }, () => waits++).Verify("new")
        );
        Assert.Equal(0, waits);
    }
}
