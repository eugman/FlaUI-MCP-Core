using FlaUI.Core.Definitions;
using FlaUI.Mcp.Core;
using FlaUI.Mcp.Tools;
using Xunit;

namespace FlaUI.Mcp.Tests;

public sealed class AgentHintTests
{
    [Theory]
    [InlineData(ControlType.MenuItem, "Tools", true)]
    [InlineData(ControlType.Button, "Preferences...", true)]
    [InlineData(ControlType.Button, "Open…", true)]
    [InlineData(ControlType.Button, "OK", false)]
    [InlineData(ControlType.TreeItem, "Node20", false)]
    public void MenusAndEllipsisCommandsDefaultToPhysicalClicks(ControlType type, string name, bool expected) =>
        Assert.Equal(expected, ClickTool.PrefersPhysical(type, name));

    [Fact]
    public void FillWarnsOnlyWhenTheValueReadsBackDifferently()
    {
        Assert.Equal("", FillTool.FillMismatch("C:\\a.csx", "C:\\a.csx", ControlType.Edit));
        Assert.Equal("", FillTool.FillMismatch("C:\\a.csx", null, ControlType.Edit));
        Assert.Contains("fill its Edit child", FillTool.FillMismatch("C:\\a.csx", "", ControlType.ComboBox));
        Assert.DoesNotContain("Edit child", FillTool.FillMismatch("x", "y", ControlType.Edit));
    }

    [Fact]
    public void PageKeyAliasesMapToPageUpAndPageDown()
    {
        var sequence = new SendKeysTool(new ElementRegistry()).PrepareSequence(["Next", "PgDn", "Prior", "PgUp"]);
        Assert.Equal(4, sequence.Count);
        Assert.Equal(sequence[0].Keys, sequence[1].Keys);
        Assert.Equal(sequence[2].Keys, sequence[3].Keys);
    }

    [Fact]
    public void NewWindowTitleReportsOnlyATitledWindowThatWasNotThereBefore()
    {
        Win32WindowInfo[] before = [new(1, "Main", 7, true, false, false)];
        Assert.Null(ClickTool.NewWindowTitle(before, before));
        Assert.Null(ClickTool.NewWindowTitle(before, [.. before, new(2, "", 7, true, false, false)]));
        Assert.Equal(
            "Preferences",
            ClickTool.NewWindowTitle(before, [.. before, new(3, "Preferences", 7, true, false, false)])
        );
    }

    [Fact]
    public void FindHintsOnlyWhenAFilteredSearchMatchesNothing()
    {
        var empty = new QueryResult([], 10, false, 0);
        Assert.Contains("value rather than name", FindTool.EmptyHint(new ElementSelector(Name: "Text Editors"), empty));
        Assert.Null(FindTool.EmptyHint(new ElementSelector(), empty));
        var one = new QueryResult(
            [new ElementInfo("w1e1", "OK", "", "Button", "", true, false, null, [], 1)],
            1,
            false,
            0
        );
        Assert.Null(FindTool.EmptyHint(new ElementSelector(Name: "OK"), one));
    }

    [Theory]
    [InlineData("", new[] { "" })]
    [InlineData("one", new[] { "one" })]
    [InlineData("a\r\nb", new[] { "a", "b" })]
    [InlineData("a\rb\n", new[] { "a", "b", "" })]
    public void TypedLineBreaksBecomeOneEnterEach(string text, string[] lines) =>
        Assert.Equal(lines, TypeTool.Lines(text));

    [Fact]
    public void SpacedAndContextMenuKeyNamesResolve()
    {
        var tool = new SendKeysTool(new ElementRegistry());
        Assert.Equal(tool.PrepareSequence(["PageDown"])[0].Keys, tool.PrepareSequence(["Page Down"])[0].Keys);
        Assert.Equal([FlaUI.Core.WindowsAPI.VirtualKeyShort.APPS], tool.PrepareSequence(["Context Menu"])[0].Keys);
        Assert.Equal(tool.PrepareSequence(["Apps"])[0].Keys, tool.PrepareSequence(["Context Menu"])[0].Keys);
    }

    [Fact]
    public void ScopeMissesNameTheSameTypeControlsPresent()
    {
        ElementInfo Toolbar(string name, string id) => new("w1e1", name, id, "ToolBar", "", true, false, null, [], 1);
        Assert.Equal(
            " ToolBar controls present: \"Tools\", automationId \"bar2\".",
            ElementQuery.DescribeCandidates("ToolBar", [Toolbar("Tools", "bar1"), Toolbar("", "bar2")])
        );
        Assert.Contains("No ToolBar controls", ElementQuery.DescribeCandidates("ToolBar", []));
    }

    [Fact]
    public void LaunchFailureNamesWindowsAndNeverSuggestsRelaunch()
    {
        var running = SessionManager.LaunchFailure("contoso.exe", 7, false, ["Use a Workspace Database?"]);
        Assert.Contains("\"Use a Workspace Database?\"", running);
        Assert.Contains("Do not blindly relaunch", running);
        Assert.Contains("has exited", SessionManager.LaunchFailure("contoso.exe", 7, true, []));
        var slow = SessionManager.LaunchFailure("contoso.exe", 7, false, [], timedOut: true);
        Assert.Contains("did not respond to UI Automation in time", slow);
        Assert.DoesNotContain("no unique owned window", slow);
    }

    [Fact]
    public void PopupMenuItemSearchesExplainThatEntriesAreButtons()
    {
        var submenus = new QueryResult(
            [new ElementInfo("w1e1", "Debug", "", "MenuItem", "", true, false, null, [], 1)],
            1,
            false,
            0
        );
        var menuItems = new ElementSelector(ControlType: "MenuItem");
        Assert.Contains("often Buttons", FindTool.PopupHint(menuItems, submenus, includeOwned: true));
        Assert.Null(FindTool.PopupHint(menuItems, submenus, includeOwned: false));
        Assert.Null(FindTool.PopupHint(new ElementSelector(ControlType: "Button"), submenus, includeOwned: true));
    }

    [Fact]
    public void TruncatedValuesPointToGetText()
    {
        ElementInfo Row(bool truncated) =>
            new("w1e1", "", "", "Edit", "", true, false, "text", [], 1, ValueTruncated: truncated);
        Assert.Contains(
            "windows_get_text",
            FindTool.TruncationHint(new QueryResult([Row(false), Row(true)], 2, false, 0))
        );
        Assert.Null(FindTool.TruncationHint(new QueryResult([Row(false)], 1, false, 0)));
    }

    [Fact]
    public void ClickFailuresNeverHaveAnEmptyReasonAndNameABlockingDialog()
    {
        Win32WindowInfo[] blocked =
        [
            new(1, "Main", 7, false, false, false),
            new(2, "Error report", 7, true, false, false),
        ];
        Assert.Equal("COMException", ToolsClickFailure("", "COMException", []));
        var withModal = ToolsClickFailure("", "COMException", blocked);
        Assert.StartsWith("COMException. Window \"Error report\"", withModal);
        Assert.Equal(
            "Element is offscreen",
            ToolsClickFailure("Element is offscreen", "X", [new(1, "Main", 7, true, false, false)])
        );
    }

    private static string ToolsClickFailure(string message, string type, IReadOnlyList<Win32WindowInfo> windows) =>
        ClickTool.ClickFailure(message, type, windows);

    [Fact]
    public void EmptyWindowListMentionsAnActiveAllowlist()
    {
        Assert.Equal(
            "No windows found (app allowlist active: ContosoStudio)",
            ListWindowsTool.EmptyMessage(new ProcessPolicy(["ContosoStudio"]))
        );
        Assert.Equal("No windows found", ListWindowsTool.EmptyMessage(ProcessPolicy.AllowAll));
    }
}
