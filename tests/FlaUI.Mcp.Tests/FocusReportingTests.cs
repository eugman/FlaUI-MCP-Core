using FlaUI.Mcp.Core;
using FlaUI.Mcp.Tools;
using Xunit;

namespace FlaUI.Mcp.Tests;

/// <summary>
/// Ref-less and handle-only keyboard input goes wherever focus happens to be. These cover the
/// reporting and precondition decisions that make that visible instead of silent.
/// </summary>
public sealed class FocusReportingTests
{
    private static FocusedControl Scintilla(int processId = 1) =>
        new(
            Hwnd: 42,
            ClassName: "WindowsForms10.Scintilla.app.0.2b89eaa",
            RootHwnd: 7,
            RootTitle: "Sales - Contoso Studio",
            ProcessId: processId
        );

    [Fact]
    public void FocusDescriptionNamesTheClassAndWindow()
    {
        var description = SendKeysTool.DescribeFocus(Scintilla(), ProcessPolicy.AllowAll);
        Assert.Contains("Scintilla", description);
        Assert.Contains("Contoso Studio", description);
    }

    [Fact]
    public void UnreportedFocusSaysSoRatherThanClaimingATarget()
    {
        Assert.Contains("not reported", SendKeysTool.DescribeFocus(null, ProcessPolicy.AllowAll));
    }

    [Fact]
    public void FocusOutsideTheAllowlistIsNotDescribed()
    {
        // The foreground window can belong to any app; its title must not leak through the reply.
        var policy = new ProcessPolicy(new[] { "ContosoStudio" });
        var description = SendKeysTool.DescribeFocus(Scintilla(processId: 0), policy);
        Assert.Contains("outside the app allowlist", description);
        Assert.DoesNotContain("Sales", description);
        Assert.DoesNotContain("Scintilla", description);
    }

    [Fact]
    public void UntitledWindowIsDescribedWithoutAnEmptyQuote()
    {
        var focus = Scintilla() with { RootTitle = "" };
        Assert.Contains("untitled window", SendKeysTool.DescribeFocus(focus, ProcessPolicy.AllowAll));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void NoExpectationSkipsTheCheck(string? expected)
    {
        Assert.Null(SendKeysTool.FocusPreconditionFailure(Scintilla(), expected, ProcessPolicy.AllowAll));
    }

    [Fact]
    public void MatchingClassPasses()
    {
        var focus = Scintilla();
        Assert.Null(SendKeysTool.FocusPreconditionFailure(focus, focus.ClassName, ProcessPolicy.AllowAll));
    }

    [Fact]
    public void MismatchedClassDiagnosesWithoutClaimingWhatWasSent()
    {
        // The diagnosis says only what is wrong. What reached the desktop depends on how far the
        // sequence got, which only the caller knows, so AbortMessage adds it.
        var failure = SendKeysTool.FocusPreconditionFailure(Scintilla(), "TreeListView", ProcessPolicy.AllowAll);
        Assert.NotNull(failure);
        Assert.Contains("TreeListView", failure);
        Assert.DoesNotContain("dispatched", failure);
    }

    [Fact]
    public void ClassComparisonIsOrdinalNotCaseInsensitive()
    {
        // Window classes are case-sensitive; a near miss must fail rather than silently pass.
        var focus = Scintilla();
        Assert.NotNull(
            SendKeysTool.FocusPreconditionFailure(focus, focus.ClassName.ToUpperInvariant(), ProcessPolicy.AllowAll)
        );
    }

    [Fact]
    public void UnknownFocusFailsAnExpectationInsteadOfAssumingItHolds()
    {
        var failure = SendKeysTool.FocusPreconditionFailure(null, "Scintilla", ProcessPolicy.AllowAll);
        Assert.NotNull(failure);
        Assert.Contains("could not be checked", failure);
    }

    [Fact]
    public void AbortBeforeTheFirstChordSaysNothingWasDispatched() =>
        Assert.Contains("No keyboard input was dispatched", SendKeysTool.AbortMessage("Mismatch.", 0, 3));

    [Fact]
    public void AMidSequenceAbortDoesNotAlsoClaimNothingWasSent()
    {
        // A Ctrl+S sequence that stopped after two chords used to report "No keyboard input was
        // dispatched. Completed 2 chord(s) before focus moved." — the false half first. A caller
        // reading that concludes the desktop is untouched.
        var message = SendKeysTool.AbortMessage("Mismatch.", 2, 5);
        Assert.Contains("Completed 2 chord(s)", message);
        Assert.Contains("remaining 3 were not sent", message);
        Assert.DoesNotContain("No keyboard input was dispatched", message);
    }
}
