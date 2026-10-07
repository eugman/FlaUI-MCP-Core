using FlaUI.Mcp.Core;
using FlaUI.Mcp.Tools;
using Xunit;

namespace FlaUI.Mcp.Tests;

/// <summary>
/// The clipboard is global desktop state, so a read is not scoped by a window handle the way every
/// other tool is. Under an allowlist that would hand back whatever was last copied from an
/// application the allowlist exists to keep this server away from.
///
/// These are pure: the first version drove the real clipboard and the real focused window, which
/// has neither on a CI runner, and asserted things that could not fail on a developer's desktop.
/// </summary>
public sealed class ClipboardReadTests
{
    [Fact]
    public void TheToolIsSchemaFreeAndSaysWhatItIsFor()
    {
        var tool = new GetClipboardTool();
        Assert.Equal("windows_get_clipboard", tool.Name);
        Assert.Contains("Scintilla", tool.Description);
        // The allowlist behaviour is part of the contract, so the caller learns it before being refused.
        Assert.Contains("allowlist", tool.Description);
    }

    [Fact]
    public void AnUnrestrictedServerReadsWithoutAnOriginCheck()
    {
        // No allowlist means no app to be kept away from.
        Assert.Null(GetClipboardTool.OwnerDenied(ProcessPolicy.AllowAll, 4242));
    }

    [Fact]
    public void TextFromAProcessOutsideTheAllowlistIsRefused()
    {
        var policy = new ProcessPolicy(["some-app-that-is-not-running"]);
        var denial = GetClipboardTool.OwnerDenied(policy, Environment.ProcessId + 1);
        Assert.NotNull(denial);
        // Whatever was on the clipboard must not travel in the refusal.
        Assert.Contains("clipboard is shared", denial);
    }

    [Fact]
    public void AnUnidentifiableOriginIsRefusedRatherThanAssumedSafe()
    {
        // The owner exited, or the text was set without an owning window. Either way its origin is
        // unknown, and unknown is not evidence that an allowed app put it there.
        var denial = GetClipboardTool.OwnerDenied(new ProcessPolicy(["contoso"]), 0);
        Assert.NotNull(denial);
        Assert.Contains("could not be identified", denial);
    }

    [Fact]
    public void TextThisServerPlacedThereIsReadableUnderAnAllowlist()
    {
        // windows_paste and windows_set_clipboard write through a message-only window owned by
        // this process; refusing to read that back would break the paste-then-verify loop.
        Assert.Null(GetClipboardTool.OwnerDenied(new ProcessPolicy(["contoso"]), Environment.ProcessId));
    }

    [Fact]
    public void FocusIsNotTheOriginCheck()
    {
        // The gate used to read the focused window, which windows_focus can move to an allowed app
        // before reading anyone's clipboard. Pin the fix: the owner decides, and a disallowed owner
        // is refused no matter what holds focus.
        var policy = new ProcessPolicy(["contoso"]);
        Assert.NotNull(GetClipboardTool.OwnerDenied(policy, Environment.ProcessId + 1));
    }

    [Theory]
    [InlineData("abcdef", 6, "abcdef")]
    [InlineData("abcdef", 3, "abc")]
    public void TruncationCutsAtTheCap(string text, int cap, string expected) =>
        Assert.Equal(expected, Win32Desktop.Truncate(text, cap));

    [Fact]
    public void TruncationDoesNotSplitASurrogatePair()
    {
        // Cutting between the halves of an emoji yields a lone surrogate, which is not valid text
        // and can break a caller's comparison in a way that looks like a content mismatch.
        var text = "ab\U0001F600cd";
        var cut = Win32Desktop.Truncate(text, 3);
        Assert.Equal("ab", cut);
        Assert.DoesNotContain(cut, c => char.IsSurrogate(c));
    }
}
