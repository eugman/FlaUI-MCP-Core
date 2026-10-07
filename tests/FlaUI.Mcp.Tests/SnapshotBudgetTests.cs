using System.Text;
using FlaUI.Mcp.Core;
using Xunit;

namespace FlaUI.Mcp.Tests;

public sealed class SnapshotBudgetTests
{
    [Fact]
    public void NodeLimitStopsFurtherVisitsAndMarksPartial()
    {
        var budget = new SnapshotBudget(2, 500);
        Assert.True(budget.Visit());
        Assert.True(budget.Visit());
        Assert.False(budget.Partial);
        Assert.False(budget.Visit());
        Assert.True(budget.Partial);
        Assert.True(budget.Exhausted);
        Assert.False(budget.Visit());
    }

    [Fact]
    public void OutputLimitDoesNotEmitTruncatedRefLines()
    {
        var budget = new SnapshotBudget(10, 256);
        var text = new StringBuilder();
        Assert.True(budget.Append(text, "- button [ref=w1e1]"));
        var previous = text.ToString();
        Assert.False(budget.Append(text, new string('x', 256)));
        Assert.Equal(previous, text.ToString());
        Assert.True(budget.Partial);
        Assert.True(budget.Exhausted);
    }

    [Theory]
    [InlineData(0, 256)]
    [InlineData(1, 255)]
    [InlineData(100001, 256)]
    [InlineData(1, 1000001)]
    public void InvalidLimitsFailBeforeTraversal(int nodes, int characters) =>
        Assert.Throws<ArgumentException>(() => new SnapshotBudget(nodes, characters));
}
