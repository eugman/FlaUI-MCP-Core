using FlaUI.Mcp.Tools;
using Xunit;

namespace FlaUI.Mcp.Tests;

public sealed class FillFallbackTests
{
    [Theory]
    [InlineData("", "select,delete")]
    [InlineData("replacement", "select,type:replacement")]
    public void ReplacesSelectionWithExactlyOneGuardedOperation(string value, string expected)
    {
        var calls = new List<string>();
        FillTool.ReplaceByKeyboard(
            value,
            () => calls.Add("select"),
            () => calls.Add("delete"),
            text => calls.Add("type:" + text)
        );
        Assert.Equal(expected, string.Join(",", calls));
    }

    [Fact]
    public void FailedSelectionDoesNotDeleteOrType()
    {
        var mutated = false;
        Assert.Throws<InvalidOperationException>(() =>
            FillTool.ReplaceByKeyboard(
                "",
                () => throw new InvalidOperationException("Focus lost"),
                () => mutated = true,
                _ => mutated = true
            )
        );
        Assert.False(mutated);
    }

    [Fact]
    public void FailedDeleteGuardPropagatesWithoutTyping()
    {
        var typed = false;
        Assert.Throws<OperationCanceledException>(() =>
            FillTool.ReplaceByKeyboard("", () => { }, () => throw new OperationCanceledException(), _ => typed = true)
        );
        Assert.False(typed);
    }
}
