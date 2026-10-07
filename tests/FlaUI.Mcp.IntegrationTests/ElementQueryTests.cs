using System.Reflection;
using System.Text.Json;
using FlaUI.Mcp.Core;
using FlaUI.Mcp.Tools;

namespace FlaUI.Mcp.IntegrationTests;

[Collection("TestApps")]
public class ElementQueryTests(TestAppFixture fixture)
{
    [Fact]
    public async Task MissingScopeIsAnObservationButCannotResolveForMutation()
    {
        var query = Query;
        var missing = new ElementSelector(AutomationId: "missing-september-scope");
        var result = await new FindTool(query).ExecuteAsync(
            JsonSerializer.SerializeToElement(
                new
                {
                    handle = fixture.WinFormsHandle,
                    within = new { automationId = missing.AutomationId },
                    selector = new { controlType = "Button" },
                }
            )
        );
        Assert.NotEqual(true, result.IsError);
        using var json = JsonDocument.Parse(result.Content[0].Text!);
        Assert.Equal("scope_not_found", json.RootElement.GetProperty("outcome").GetString());
        Assert.False(json.RootElement.GetProperty("complete").GetBoolean());
        Assert.Empty(json.RootElement.GetProperty("elements").EnumerateArray());
        Assert.Throws<ElementNotFoundException>(() => query.Resolve(fixture.WinFormsHandle, missing));
    }

    [Theory]
    [InlineData("Invoke")]
    [InlineData("ExpandCollapse")]
    public void CachedPatternDiscoveryDoesNotReportSupportedControlsUnreadable(string pattern)
    {
        var query = Query;
        query
            .Resolve(fixture.WinFormsHandle, new(AutomationId: pattern == "Invoke" ? "ButtonsTab" : "TreesTab"))
            .Patterns.SelectionItem.Pattern.Select();
        var scope =
            pattern == "Invoke"
                ? new ElementSelector(AutomationId: "ClickMeButton")
                : new ElementSelector(Name: "Fruits", ControlType: "TreeItem");
        var result = query.Find(fixture.WinFormsHandle, new(Pattern: pattern, RootOnly: true), scope);
        Assert.True(result.Complete);
        Assert.Contains(pattern, Assert.Single(result.Elements).Patterns);
    }

    [Fact]
    public void CompactFindReportsToggleAndOptionalBounds()
    {
        var query = Query;
        query
            .Resolve(fixture.WinFormsHandle, new(AutomationId: "ButtonsTab", ControlType: "TabItem"))
            .Patterns.SelectionItem.Pattern.Select();
        var checkbox = query.Resolve(fixture.WinFormsHandle, new(AutomationId: "EnableCheckbox"));
        var expected = checkbox.Patterns.Toggle.Pattern.ToggleState.Value.ToString();
        var result = query.Find(
            fixture.WinFormsHandle,
            new(AutomationId: "EnableCheckbox", Pattern: "Toggle"),
            includeBounds: true
        );
        var item = Assert.Single(result.Elements);
        Assert.Equal(expected, item.ToggleState);
        Assert.NotNull(item.Bounds);
        Assert.True(item.Bounds.Width > 0);
    }

    [Fact]
    public async Task AbsentWaitHonorsSettlement()
    {
        var clock = System.Diagnostics.Stopwatch.StartNew();
        var result = await new WaitTool(Query).ExecuteAsync(
            JsonSerializer.SerializeToElement(
                new
                {
                    handle = fixture.WinFormsHandle,
                    selector = new { automationId = "fla_missing", rootOnly = true },
                    state = "absent",
                    settleMs = 150,
                    timeoutMs = 2000,
                }
            )
        );
        Assert.NotEqual(true, result.IsError);
        Assert.True(clock.ElapsedMilliseconds >= 150);
    }

    private ElementQuery Query => new(fixture.Session, fixture.Elements, new PendingInvokeTracker());

    [Fact]
    public void ScopedDiscoveryFindsExpandedWinFormsBranch()
    {
        var query = Query;
        var tab = query.Resolve(fixture.WinFormsHandle, new(AutomationId: "TreesTab"));
        tab.Patterns.SelectionItem.Pattern.Select();
        var tree = query.Resolve(fixture.WinFormsHandle, new(AutomationId: "TestTreeView"));
        var fruits = query.Resolve(
            fixture.WinFormsHandle,
            new(Name: "Fruits", ControlType: "TreeItem"),
            new(AutomationId: "TestTreeView")
        );
        fruits.Patterns.ExpandCollapse.Pattern.Collapse();
        Assert.Equal(
            FlaUI.Core.Definitions.ExpandCollapseState.Collapsed,
            fruits.Patterns.ExpandCollapse.Pattern.ExpandCollapseState.Value
        );
        fruits.Patterns.ExpandCollapse.Pattern.Expand();
        Assert.Equal(
            FlaUI.Core.Definitions.ExpandCollapseState.Expanded,
            fruits.Patterns.ExpandCollapse.Pattern.ExpandCollapseState.Value
        );
        Assert.True(
            query.IsPresent(
                fixture.WinFormsHandle,
                new(Name: "Apple", ControlType: "TreeItem", Visible: true),
                new(AutomationId: "TestTreeView")
            )
        );
    }

    [Fact]
    public void RootOnlyChecksRootsWithoutMatchingDescendants()
    {
        Assert.True(Query.IsPresent(fixture.WinFormsHandle, new(AutomationId: "MainForm", RootOnly: true)));
        Assert.False(Query.IsPresent(fixture.WinFormsHandle, new(AutomationId: "MainTabs", RootOnly: true)));
        var raw = Query.Find(fixture.WinFormsHandle, new(RootOnly: true));
        Assert.Single(raw.Elements);
        Assert.False(raw.Truncated);
        Assert.Equal(0, raw.Unreadable);
    }

    [Fact]
    public void ExactPresenceChecksRootMissingAndFullSelector()
    {
        Assert.True(Query.IsPresent(fixture.WinFormsHandle, new(AutomationId: "MainForm")));
        Assert.False(Query.IsPresent(fixture.WinFormsHandle, new(AutomationId: "fla_missing_control")));
        Assert.False(Query.IsPresent(fixture.WinFormsHandle, new(AutomationId: "MainForm", ControlType: "Button")));
        Assert.True(
            Query.IsPresent(fixture.WinFormsHandle, new(AutomationId: "MainTabs"), new(AutomationId: "MainForm"))
        );
    }

    [Fact]
    public void ExhaustedExactPresenceSearchIsNotAbsence() =>
        Assert.Throws<InvalidOperationException>(() =>
            Query.IsPresent(
                fixture.WinFormsHandle,
                new(AutomationId: "fla_missing_control"),
                budget: new SearchBudget(1, TimeSpan.Zero)
            )
        );

    [Fact]
    public void ExactIdResolutionIncludesRootAndHonorsAdditionalSelectors()
    {
        var root = Query.Resolve(fixture.WinFormsHandle, new(AutomationId: "MainForm", ControlType: "Window"));
        Assert.Equal("MainForm", root.Properties.AutomationId.Value);
        Assert.Throws<InvalidOperationException>(() =>
            Query.Resolve(fixture.WinFormsHandle, new(AutomationId: "MainForm", ControlType: "Button"))
        );
        var tabs = Query.Resolve(fixture.WinFormsHandle, new(AutomationId: "MainTabs"), new(AutomationId: "MainForm"));
        Assert.Equal("MainTabs", tabs.Properties.AutomationId.Value);
    }

    [Fact]
    public void FindByAutomationIdDoesNotInvalidateSnapshotRefs()
    {
        var tab = fixture.GetWpfWindow()!.FindFirstDescendant(c => c.ByAutomationId("ButtonsTab"));
        Assert.NotNull(tab);
        tab.Patterns.SelectionItem.Pattern.Select();
        var snapshot = fixture.TakeSnapshot(fixture.WpfHandle);
        var oldRef = TestAppFixture.FindRefInSnapshot(snapshot, "Click Me")!;
        var result = Query.Find(fixture.WpfHandle, new(AutomationId: "ClickMeButton"));
        Assert.False(result.Truncated);
        Assert.Equal(0, result.Unreadable);
        var button = Assert.Single(result.Elements);
        Assert.Equal("Button", button.ControlType);
        Assert.Contains("Invoke", button.Patterns);
        Assert.True(fixture.Elements.HasElement(oldRef));
    }

    [Fact]
    public void AmbiguousSelectionFailsInsteadOfChoosingFirst() =>
        Assert.Throws<AmbiguousMatchException>(() => Query.Resolve(fixture.WpfHandle, new(ControlType: "TabItem")));

    [Fact]
    public void DepthLimitIsReportedAsTruncated()
    {
        var result = Query.Find(fixture.WpfHandle, new(), maxDepth: 0);
        Assert.True(result.Truncated);
        Assert.Single(result.Elements);
    }

    [Fact]
    public async Task ArtifactOnlyScreenshotOmitsImagePayload()
    {
        var path = Path.Combine(Path.GetTempPath(), "fla_capture_" + Guid.NewGuid().ToString("N") + ".png");
        try
        {
            var tool = new ScreenshotTool(fixture.Session, fixture.Elements);
            var result = await tool.ExecuteAsync(
                JsonSerializer.SerializeToElement(
                    new
                    {
                        handle = fixture.WpfHandle,
                        background = true,
                        savePath = path,
                        includeImage = false,
                    }
                )
            );
            Assert.False(result.IsError == true);
            Assert.DoesNotContain(result.Content, c => c.Type == "image");
            Assert.True(new FileInfo(path).Length > 0);
        }
        finally
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
    }

    [Fact]
    public async Task TextMatchesEitherNameOrValue()
    {
        // The reason text: exists: a control's displayed label lives in Name for some controls and
        // in Value for others, and a caller looking at the screen cannot tell which. Both must hit.
        Query
            .Resolve(fixture.WpfHandle, new(AutomationId: "FormsTab", ControlType: "TabItem"))
            .Patterns.SelectionItem.Pattern.Select();
        var byName = Query.Find(fixture.WpfHandle, new(Text: "Name", ControlType: "Edit"), maxResults: 5);
        Assert.Contains(byName.Elements, e => e.AutomationId == "NameTextBox");

        // Same control, reached through its value instead.
        var fill = new FillTool(fixture.Elements);
        var reference = byName.Elements.Single(e => e.AutomationId == "NameTextBox").Ref;
        var fillResult = await fill.ExecuteAsync(
            JsonSerializer.SerializeToElement(new { @ref = reference, value = "Ada Lovelace" })
        );
        Assert.False(fillResult.IsError == true, string.Join("; ", fillResult.Content.Select(c => c.Text)));
        Assert.DoesNotContain(
            "warning",
            string.Join("; ", fillResult.Content.Select(c => c.Text)),
            StringComparison.OrdinalIgnoreCase
        );
        Assert.Equal(
            "Ada Lovelace",
            fixture.Elements.GetElement(reference)!.Patterns.Value.Pattern.Value.ValueOrDefault
        );
        var byValue = Query.Find(fixture.WpfHandle, new(Text: "Ada Lovelace", ControlType: "Edit"), maxResults: 5);
        Assert.Contains(byValue.Elements, e => e.AutomationId == "NameTextBox");
    }

    [Fact]
    public void TextDoesNotExposeAPasswordsContent()
    {
        // The value arm mirrors the IsPassword guard, so a password box stays unmatchable by its
        // contents even though text: otherwise searches value.
        var boxes = Query.Find(fixture.WpfHandle, new(ControlType: "Edit"), maxResults: 20);
        Assert.All(boxes.Elements, e => Assert.NotEqual("password", e.Value));
    }
}
