using System.Text.Json;
using FlaUI.Mcp.Core;
using FlaUI.Mcp.Tools;
using Xunit;

namespace FlaUI.Mcp.Tests;

/// <summary>
/// Grid, tree and list rows often carry a positional name with the displayed label in value, such as
/// "Name row 87"/"Gross Sales" or "Node0"/"Auto Formatting".
/// A name-based search silently matches nothing, so text: matches either.
/// </summary>
public sealed class TextSelectorTests
{
    private static QueryResult Empty() => new([], 10, false, 0);

    [Fact]
    public void TextIsAcceptedFromSelectorJson()
    {
        var selector = JsonSerializer.Deserialize<ElementSelector>(
            """{"text":"Gross Sales","controlType":"DataItem"}""",
            FindTool.SelectorOptions
        );
        Assert.Equal("Gross Sales", selector!.Text);
        Assert.Equal("DataItem", selector.ControlType);
    }

    [Fact]
    public void TextIsAppendedSoPositionalMeaningIsUnchanged()
    {
        // Callers compare selectors by record equality and build them positionally in
        // places; a field inserted earlier would silently change what those comparisons mean.
        var selector = new ElementSelector("id1", "name1", "Button");
        Assert.Equal("id1", selector.AutomationId);
        Assert.Equal("name1", selector.Name);
        Assert.Equal("Button", selector.ControlType);
        Assert.Null(selector.Text);
    }

    [Fact]
    public void SelectorEqualityStillHolds()
    {
        Assert.Equal(new ElementSelector(AutomationId: "treeList"), new ElementSelector(AutomationId: "treeList"));
        Assert.NotEqual(new ElementSelector(Text: "a"), new ElementSelector(Text: "b"));
    }

    [Fact]
    public void ATextOnlyMissGetsAHint()
    {
        // The trigger previously looked only at name/controlType/value, so a text-only search
        // returned no guidance at all.
        var hint = FindTool.EmptyHint(new ElementSelector(Text: "Gross Sales"), Empty());
        Assert.NotNull(hint);
        Assert.Contains("exact and case-sensitive", hint);
    }

    [Fact]
    public void TheHintDoesNotTellATextUserToTryText()
    {
        // Advising text: to someone who just used it wastes the one sentence they get.
        var afterText = FindTool.EmptyHint(new ElementSelector(Text: "Gross Sales"), Empty())!;
        Assert.DoesNotContain("try text:", afterText);
        Assert.Contains("already matches name and value", afterText);

        var beforeText = FindTool.EmptyHint(new ElementSelector(Name: "Gross Sales"), Empty())!;
        Assert.Contains("try text:", beforeText);
    }

    [Fact]
    public void TheHintStillFiresForTheOlderSelectorFields()
    {
        Assert.NotNull(FindTool.EmptyHint(new ElementSelector(Name: "Delete"), Empty()));
        Assert.NotNull(FindTool.EmptyHint(new ElementSelector(ControlType: "MenuItem"), Empty()));
        Assert.NotNull(FindTool.EmptyHint(new ElementSelector(Value: "Sales"), Empty()));
    }

    [Fact]
    public void NoHintWhenSomethingMatched()
    {
        var found = new QueryResult([new("w1e1", "n", "", "Button", "", true, false, null, [], 1)], 10, false, 0);
        Assert.Null(FindTool.EmptyHint(new ElementSelector(Text: "n"), found));
    }

    [Fact]
    public void NoHintWithoutATextualSelector()
    {
        // An automationId-only miss says nothing about name vs value.
        Assert.Null(FindTool.EmptyHint(new ElementSelector(AutomationId: "treeList"), Empty()));
    }
}
