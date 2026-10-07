using System.Text.Json;
using FlaUI.Mcp.Core;
using FlaUI.Mcp.Tools;
using Xunit;

namespace FlaUI.Mcp.Tests;

/// <summary>
/// windows_paste reported "Pasted N characters" whenever SendInput did not throw. Ctrl+V is posted,
/// not processed, so a read-only editor or an app that binds Ctrl+V elsewhere produced the same
/// message. The reply now says what it actually knows.
/// </summary>
public sealed class PasteEvidenceTests
{
    [Fact]
    public void ARefLessPasteSaysItCannotBeVerified()
    {
        // Nothing to read back, so the reply must not imply the text arrived.
        var evidence = PasteTool.PasteEvidence(null, "foreach(var m in Selected.Measures)", null);
        Assert.Contains("Not verified", evidence);
        Assert.Contains("no ref was given", evidence);
    }

    [Fact]
    public void AnEmptyPasteIsNotVerifiable()
    {
        // Contains("") is always true, so the old read-back reported "Verified" for a paste that
        // could not have changed anything.
        Assert.Contains("Not verified", PasteTool.DescribeEvidence("existing", "existing", ""));
    }

    [Fact]
    public void TextTheControlAlreadyHeldIsNotEvidenceOfAPaste()
    {
        // Pasting "Sales" into a read-only field already reading "Total Sales" leaves the value
        // untouched and still passes a containment test. Short pastes into code editors are the
        // common case, and this is the tool the docs recommend for code.
        var evidence = PasteTool.DescribeEvidence("Total Sales", "Total Sales", "Sales");
        Assert.Contains("did not change", evidence);
        Assert.DoesNotContain("Verified:", evidence);
    }

    [Fact]
    public void AChangedValueContainingTheTextIsVerified()
    {
        Assert.Contains("Verified:", PasteTool.DescribeEvidence("", "Total Sales", "Total Sales"));
    }

    [Fact]
    public void AChangeThatDoesNotContainTheTextIsAWarning()
    {
        // The app reformatted it, or a second paste overtook the first.
        var evidence = PasteTool.DescribeEvidence("", "something else", "Total Sales");
        Assert.Contains("Warning", evidence);
        Assert.DoesNotContain("Verified:", evidence);
    }

    [Fact]
    public void AnUnreadableBeforeValueIsNotVerified()
    {
        // Without the prior value a match cannot be attributed to the paste.
        Assert.Contains("Not verified", PasteTool.DescribeEvidence(null, "Total Sales", "Total Sales"));
    }

    [Fact]
    public async Task VerifyFocusStillRequiresARef()
    {
        var tool = new PasteTool(new ElementRegistry(), new PendingInvokeTracker(), ProcessPolicy.AllowAll, null);
        var result = await tool.ExecuteAsync(JsonSerializer.SerializeToElement(new { text = "x", verifyFocus = true }));
        Assert.True(result.IsError);
        Assert.Contains("nothing was pasted", result.Content[0].Text);
    }

    [Fact]
    public async Task MissingTextIsRejected()
    {
        var tool = new PasteTool(new ElementRegistry(), new PendingInvokeTracker(), ProcessPolicy.AllowAll, null);
        var result = await tool.ExecuteAsync(JsonSerializer.SerializeToElement(new { }));
        Assert.True(result.IsError);
        Assert.Contains("text", result.Content[0].Text);
    }

    [Fact]
    public async Task AHandleWithoutASessionManagerIsRejected()
    {
        var tool = new PasteTool(new ElementRegistry(), new PendingInvokeTracker(), ProcessPolicy.AllowAll, null);
        var result = await tool.ExecuteAsync(JsonSerializer.SerializeToElement(new { text = "x", handle = "w1" }));
        Assert.True(result.IsError);
        Assert.Contains("session manager", result.Content[0].Text);
    }

    [Fact]
    public void TheToolAdvertisesFocusVerification()
    {
        // It is the tool recommended for code, so the option that stops a paste landing in the wrong
        // control has to be discoverable from the schema.
        var schema = JsonSerializer.Serialize(
            new PasteTool(new ElementRegistry(), new PendingInvokeTracker(), ProcessPolicy.AllowAll, null).InputSchema
        );
        Assert.Contains("verifyFocus", schema);
    }
}
