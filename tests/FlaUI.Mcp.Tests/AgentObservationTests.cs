using System.Text.Json;
using FlaUI.Mcp;
using FlaUI.Mcp.Core;
using FlaUI.Mcp.Tools;
using Xunit;

namespace FlaUI.Mcp.Tests;

public sealed class AgentObservationTests
{
    [Fact]
    public void ExplicitNodeLimitCannotBeBypassedBySuppliedBudget()
    {
        var budget = new SearchBudget(1000, TimeSpan.FromSeconds(10));
        budget.Visit();
        budget.LimitRemaining(2);
        Assert.Equal(2, budget.Remaining);
        budget.LimitRemaining(100);
        Assert.Equal(2, budget.Remaining);
        budget.Visit();
        budget.Visit();
        Assert.False(budget.Available);
    }

    private static ElementInfo Item(
        string? value = null,
        bool truncated = false,
        bool? selected = null,
        string? toggle = null
    ) => new("w1e1", "control", "", "CheckBox", "", true, false, value, ["Toggle"], 1, selected, truncated, toggle);

    [Theory]
    [InlineData(true, 0)]
    [InlineData(false, 1)]
    public void IncompleteSearchCannotProveAbsence(bool truncated, int unreadable) =>
        Assert.False(WaitTool.Satisfied(new([], 1, truncated, unreadable), "absent", default));

    [Fact]
    public void CompleteEmptySearchCanProveAbsenceWithinItsScope() =>
        Assert.True(WaitTool.Satisfied(new([], 1, false, 0), "absent", default));

    [Fact]
    public void ToggleCapabilityDoesNotProveOff()
    {
        var expected = JsonSerializer.SerializeToElement("Off");
        Assert.False(WaitTool.Satisfied(new([Item()], 1, false, 0), "toggle", expected));
        Assert.True(WaitTool.Satisfied(new([Item(toggle: "Off")], 1, false, 0), "toggle", expected));
    }

    [Fact]
    public void TruncatedValueAndUnknownSelectionCannotMatch()
    {
        Assert.False(
            WaitTool.Satisfied(
                new([Item("x", truncated: true)], 1, false, 0),
                "value",
                JsonSerializer.SerializeToElement("x")
            )
        );
        Assert.False(
            WaitTool.Satisfied(new([Item()], 1, false, 0), "selected", JsonSerializer.SerializeToElement(false))
        );
        Assert.True(
            WaitTool.Satisfied(
                new([Item(selected: false)], 1, false, 0),
                "selected",
                JsonSerializer.SerializeToElement(false)
            )
        );
    }

    [Fact]
    public void AmbiguousStateCannotMatch() =>
        Assert.False(
            WaitTool.Satisfied(
                new([Item(toggle: "On"), Item(toggle: "On")], 2, false, 0),
                "toggle",
                JsonSerializer.SerializeToElement("On")
            )
        );

    [Theory]
    [InlineData("{}")]
    [InlineData("{\"handle\":null}")]
    [InlineData("{\"handle\":\"w1\",\"selector\":null}")]
    [InlineData("{\"handle\":\"w1\",\"within\":null}")]
    public async Task MalformedFindReturnsAnErrorWithoutProviderAccess(string json)
    {
        var result = await new FindTool(null!).ExecuteAsync(JsonDocument.Parse(json).RootElement);
        Assert.True(result.IsError);
        Assert.Contains("windows_find", result.Content[0].Text);
    }

    [Fact]
    public void InvalidPatternFailsValidation() =>
        Assert.Throws<ArgumentException>(() => ElementQuery.ValidateSelector(new(Pattern: "Typo")));

    [Fact]
    public void CompactObservationOmitsBoundsAndUnknownState()
    {
        // An absent state field means unknown; serialized nulls would only cost tokens.
        var unknown = JsonSerializer.Serialize(Item(), McpProtocol.JsonOptions);
        Assert.DoesNotContain("bounds", unknown);
        Assert.DoesNotContain("toggleState", unknown);
        Assert.Contains(
            "\"toggleState\":\"Off\"",
            JsonSerializer.Serialize(Item(toggle: "Off"), McpProtocol.JsonOptions)
        );
    }

    [Fact]
    public async Task StrictFocusRequiresARefBeforeAnyInput()
    {
        var type = await new TypeTool(new()).ExecuteAsync(
            JsonSerializer.SerializeToElement(new { text = "x", verifyFocus = true })
        );
        var keys = await new SendKeysTool(new()).ExecuteAsync(
            JsonSerializer.SerializeToElement(new { chord = "Enter", verifyFocus = true })
        );
        Assert.True(type.IsError);
        Assert.True(keys.IsError);
        Assert.Contains("no input sent", type.Content[0].Text);
        Assert.Contains("no input sent", keys.Content[0].Text);
    }
}
