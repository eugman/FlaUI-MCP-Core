using System.Text.Json;
using FlaUI.Mcp.Core;
using FlaUI.Mcp.Tools;
using Xunit;

namespace FlaUI.Mcp.Tests;

public sealed class KeySequenceValidationTests
{
    [Fact]
    public async Task HandleWithoutSessionsReturnsActionableError()
    {
        var tool = new SendKeysTool(new ElementRegistry());
        var result = await tool.ExecuteAsync(JsonSerializer.SerializeToElement(new { handle = "w1", chord = "Enter" }));
        Assert.True(result.IsError);
        Assert.Contains("requires a session manager", result.Content[0].Text);
    }

    [Theory]
    [InlineData("unsupported", "Unsupported key")]
    [InlineData("Code Actions", "use windows_type")]
    [InlineData("", "No keys were parsed")]
    [InlineData("Win+R", "Windows key is disabled")]
    public async Task InvalidLaterChordFailsBeforeTargetResolution(string later, string error)
    {
        // No sessions or desktop provider: resolving the supplied handle would fail.
        // The validation error proves that execution did not reach target resolution.
        var tool = new SendKeysTool(new ElementRegistry(), processPolicy: new ProcessPolicy(["te"]));
        var result = await tool.ExecuteAsync(
            JsonSerializer.SerializeToElement(new { handle = "not-registered", keys = new[] { "Ctrl+S", later } })
        );
        Assert.True(result.IsError);
        Assert.Contains(error, result.Content[0].Text);
        Assert.Contains("Completed 0 chord(s)", result.Content[0].Text);
        Assert.Contains("No keyboard input was dispatched", result.Content[0].Text);
    }

    [Fact]
    public void ValidSequencePreservesOrderAndNormalizesSpacing()
    {
        var tool = new SendKeysTool(new ElementRegistry());
        var sequence = tool.PrepareSequence([" Ctrl + S ", "Enter"]);
        Assert.Equal(new[] { "Ctrl+S", "Enter" }, sequence.Select(step => step.Text));
        Assert.Equal(2, sequence[0].Keys.Count);
        Assert.Single(sequence[1].Keys);
    }
}
