using System.Text.Json;
using FlaUI.Mcp;
using FlaUI.Mcp.Core;
using FlaUI.Mcp.Tools;
using Xunit;

namespace FlaUI.Mcp.Tests;

public sealed class SearchAndCancellationTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void DepthLimitIsTruncatedOnlyWhenChildrenAreOmitted(bool hasChildren)
    {
        var result = BoundedSearch.Find(
            new[] { 1 },
            _ => hasChildren ? new[] { 2 } : [],
            _ => true,
            _ => null,
            new SearchBudget(10, TimeSpan.FromSeconds(1)),
            0,
            10
        );
        Assert.Single(result.Matches);
        Assert.Equal(hasChildren, result.Truncated);
    }

    [Fact]
    public void ReadingExpirationHasNoCancellationSideEffect()
    {
        var operation = new OperationContext();
        OperationContext.Current.Value = operation;
        operation.Stop.Cancel();
        try
        {
            Assert.False(new SearchBudget(10, TimeSpan.FromSeconds(1), () => TimeSpan.Zero).Expired);
        }
        finally
        {
            OperationContext.Current.Value = null;
        }
    }

    [Fact]
    public async Task RegistryTimeoutPreventsLateMutationAfterProviderReturns()
    {
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var finished = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var mutated = false;
        var registry = new ToolRegistry(TimeSpan.FromMilliseconds(100));
        registry.RegisterTool(new DelayedMutation(release.Task, finished, () => mutated = true));
        try
        {
            var result = await registry.ExecuteToolAsync("delayed", null);
            Assert.True(result.IsError);
            Assert.Contains("timed out", result.Content[0].Text);
            release.SetResult();
            await finished.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.False(mutated);
        }
        finally
        {
            release.TrySetResult();
        }
    }

    private sealed class DelayedMutation(Task release, TaskCompletionSource finished, Action mutate) : ToolBase
    {
        public override string Name => "delayed";
        public override string Description => "Fake delayed provider, no desktop access";
        public override object InputSchema => new { };

        public override async Task<McpToolResult> ExecuteAsync(JsonElement? arguments)
        {
            try
            {
                await release;
                MutationGuard.Execute(() => { }, mutate);
                return TextResult("done");
            }
            finally
            {
                finished.TrySetResult();
            }
        }
    }

    [Fact]
    public async Task CancelledBatchDoesNotDispatchNextRow()
    {
        var context = new OperationContext();
        var calls = 0;
        OperationContext.Current.Value = context;
        try
        {
            var rows = JsonSerializer.Deserialize<JsonElement[]>(
                "[{\"action\":\"snapshot\"},{\"action\":\"snapshot\"}]"
            )!;
            await Assert.ThrowsAsync<OperationCanceledException>(() =>
                BatchTool.ExecuteRows(
                    rows,
                    (_, _) =>
                    {
                        calls++;
                        context.Stop.Cancel();
                        return Task.FromResult(new McpToolResult { Content = [] });
                    },
                    stopOnError: false
                )
            );
            Assert.Equal(1, calls);
        }
        finally
        {
            OperationContext.Current.Value = null;
        }
    }

    [Fact]
    public void LogicalControlsSharingHostRemainDistinct()
    {
        var found = BoundedSearch.Find(
            new[] { 1 },
            n => n == 1 ? new[] { 2 } : [],
            n => n == 2,
            n => ElementQuery.RuntimeIdentity([42, n]),
            new SearchBudget(10, TimeSpan.FromSeconds(1)),
            4,
            3
        );
        Assert.Equal(2, Assert.Single(found.Matches).Node);
        Assert.False(found.Truncated);
        Assert.Null(ElementQuery.RuntimeIdentity([]));
        Assert.Null(ElementQuery.RuntimeIdentity(null));
    }

    [Fact]
    public void ProviderFailureIsNotSuccessfulAbsence()
    {
        var result = BoundedSearch.Find(
            new[] { 1 },
            _ => Array.Empty<int>(),
            _ => throw new InvalidOperationException("provider failed"),
            _ => null,
            new SearchBudget(10, TimeSpan.FromSeconds(1)),
            4,
            3
        );
        Assert.Empty(result.Matches);
        Assert.Equal(1, result.Unreadable);
    }

    [Fact]
    public void TimeoutDoesNotQuarantineOtherCallsButCancelsOldWork()
    {
        var operations = new OperationCoordinator();
        var first = operations.Begin("first");
        first.Stop.Cancel();
        var recovery = operations.Begin("recovery");
        Assert.NotEqual(first.Id, recovery.Id);
        OperationContext.Current.Value = first;
        try
        {
            Assert.Throws<OperationCanceledException>(OperationContext.Check);
        }
        finally
        {
            OperationContext.Current.Value = null;
        }
    }
}
