using FlaUI.Mcp.Core;
using Xunit;

namespace FlaUI.Mcp.Tests;

public sealed class ClipboardRestoreTests
{
    [Theory]
    [InlineData(false, 5u, "clipboard_busy", false, false)]
    [InlineData(true, 6u, "external_change_preserved", false, true)]
    [InlineData(true, 5u, "restored", true, true)]
    public void RestorationNeverOverwritesExternalChanges(
        bool available,
        uint sequence,
        string status,
        bool restores,
        bool closes
    )
    {
        var restored = false;
        var closed = false;
        var result = ClipboardRestore.Run(
            5,
            () => available,
            () => sequence,
            () => restored = true,
            () => closed = true
        );
        Assert.Equal(status, result);
        Assert.Equal(restores, restored);
        Assert.Equal(closes, closed);
    }

    [Fact]
    public void RestoreFailureStillReleasesClipboard()
    {
        var closed = false;
        Assert.Throws<InvalidOperationException>(() =>
            ClipboardRestore.Run(
                1,
                () => true,
                () => 1,
                () => throw new InvalidOperationException(),
                () => closed = true
            )
        );
        Assert.True(closed);
    }
}
