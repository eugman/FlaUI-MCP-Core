using FlaUI.Mcp.Core;
using Xunit;

namespace FlaUI.Mcp.Tests;

public class PatternSelectorTests
{
    [Theory]
    [InlineData("Invoke")]
    [InlineData("ExpandCollapse")]
    public void SplitButtonPatternsAreAccepted(string pattern) => ElementQuery.ValidateSelector(new(Pattern: pattern));

    [Fact]
    public void UnsupportedPatternsAreRejected() =>
        Assert.Throws<ArgumentException>(() => ElementQuery.ValidateSelector(new(Pattern: "Arbitrary")));
}
