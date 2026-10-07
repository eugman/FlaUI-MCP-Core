using System.Text.Json;
using FlaUI.Mcp;
using Xunit;

namespace FlaUI.Mcp.Tests;

public class ToolRegistryTimeoutTests
{
    [Fact]
    public async Task TargetMetadataResolutionIsInsideTimeout()
    {
        using var release = new ManualResetEventSlim();
        var finished = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var registry = new ToolRegistry(TimeSpan.FromMilliseconds(50))
        {
            ResolveTarget = _ =>
            {
                try
                {
                    release.Wait(TimeSpan.FromSeconds(5));
                    return null;
                }
                finally
                {
                    finished.TrySetResult();
                }
            },
        };
        registry.RegisterTool(new SuccessfulTool());
        try
        {
            var result = await registry.ExecuteToolAsync("successful", null).WaitAsync(TimeSpan.FromSeconds(2));
            Assert.True(result.IsError);
            Assert.Contains("timed out", result.Content[0].Text);
        }
        finally
        {
            release.Set();
            await finished.Task.WaitAsync(TimeSpan.FromSeconds(2));
        }
    }

    [Fact]
    public async Task ExecuteToolAsync_TimesOutSynchronousToolBody()
    {
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var finished = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var registry = new ToolRegistry(TimeSpan.FromMilliseconds(50));
        registry.RegisterTool(new BlockingTool(entered, release, finished));

        // The 50 ms tool timeout is what is under test. The generous waits around it only guard
        // against a starved thread pool on a busy machine or CI runner.
        var allowance = TimeSpan.FromSeconds(10);
        try
        {
            var execution = registry.ExecuteToolAsync("blocking", arguments: null);
            Assert.True(entered.Wait(allowance), "The synchronous tool body did not start.");
            var result = await execution.WaitAsync(allowance);

            Assert.True(result.IsError);
            Assert.Contains("timed out", result.Content[0].Text);
        }
        finally
        {
            release.Set();
            if (entered.IsSet)
            {
                await finished.Task.WaitAsync(allowance);
            }
        }
    }

    [Fact]
    public async Task ExecuteToolAsync_ReturnsSuccessfulToolResult()
    {
        var registry = new ToolRegistry(TimeSpan.FromSeconds(1));
        registry.RegisterTool(new SuccessfulTool());

        var result = await registry.ExecuteToolAsync("successful", arguments: null);

        Assert.NotEqual(true, result.IsError);
        Assert.Equal("ok", result.Content[0].Text);
    }

    [Fact]
    public async Task ExecuteToolAsync_InvokesActivityCallbackForKnownToolsOnly()
    {
        var activity = 0;
        var registry = new ToolRegistry(TimeSpan.FromSeconds(1), onToolActivity: () => activity++);
        registry.RegisterTool(new SuccessfulTool());

        await registry.ExecuteToolAsync("successful", arguments: null);
        await registry.ExecuteToolAsync("unknown", arguments: null);

        Assert.Equal(1, activity);
    }

    private sealed class BlockingTool : ITool
    {
        private readonly ManualResetEventSlim _entered;
        private readonly ManualResetEventSlim _release;
        private readonly TaskCompletionSource _finished;

        public BlockingTool(ManualResetEventSlim entered, ManualResetEventSlim release, TaskCompletionSource finished)
        {
            _entered = entered;
            _release = release;
            _finished = finished;
        }

        public string Name => "blocking";

        public McpTool GetDefinition() =>
            new()
            {
                Name = Name,
                Description = "Blocks synchronously",
                InputSchema = new { type = "object" },
            };

        public Task<McpToolResult> ExecuteAsync(JsonElement? arguments)
        {
            _entered.Set();
            try
            {
                _release.Wait();
                return Task.FromResult(
                    new McpToolResult
                    {
                        Content = new List<McpContent>
                        {
                            new() { Type = "text", Text = "late" },
                        },
                    }
                );
            }
            finally
            {
                _finished.TrySetResult();
            }
        }
    }

    private sealed class SuccessfulTool : ITool
    {
        public string Name => "successful";

        public McpTool GetDefinition() =>
            new()
            {
                Name = Name,
                Description = "Succeeds",
                InputSchema = new { type = "object" },
            };

        public Task<McpToolResult> ExecuteAsync(JsonElement? arguments)
        {
            return Task.FromResult(
                new McpToolResult
                {
                    Content = new List<McpContent>
                    {
                        new() { Type = "text", Text = "ok" },
                    },
                }
            );
        }
    }
}
