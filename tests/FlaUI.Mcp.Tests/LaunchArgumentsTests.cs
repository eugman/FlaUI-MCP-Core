using FlaUI.Mcp.Core;
using Xunit;

namespace FlaUI.Mcp.Tests;

public sealed class LaunchArgumentsTests
{
    [Fact]
    public void ArgumentsRetainSpacesQuotesEmptyValuesAndTrailingBackslashes()
    {
        string[] args = [@"C:\model directory\model.bim", "", "quoted \"value\"", @"C:\directory with spaces\"];
        var info = SessionManager.LaunchStartInfo("app.exe", args);
        Assert.Equal(args, info.ArgumentList);
        Assert.Empty(info.Arguments);
        Assert.Equal("app.exe", info.FileName);
    }

    [Fact]
    public void OmittedArgumentsRemainEmpty() =>
        Assert.Empty(SessionManager.LaunchStartInfo("app.exe", null).ArgumentList);
}
