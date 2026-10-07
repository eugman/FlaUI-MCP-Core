using System.Diagnostics;

namespace FlaUI.Mcp.Core;

public sealed class SearchBudget(int maxNodes, TimeSpan duration, Func<TimeSpan>? elapsed = null)
{
    private readonly Stopwatch watch = Stopwatch.StartNew();
    public int Visited { get; private set; }
    public int Remaining => Math.Max(0, maxNodes - Visited);
    public bool Expired => (elapsed?.Invoke() ?? watch.Elapsed) >= duration;
    public bool Available => Remaining > 0 && !Expired;

    public void Visit() => Visited++;

    public void LimitRemaining(int maximum)
    {
        if (maximum < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(maximum));
        }

        maxNodes = Math.Min(maxNodes, Visited + maximum);
    }
}

public sealed record SearchResult<T>(List<(T Node, int Depth)> Matches, bool Truncated, int Unreadable);

/// <summary>Provider-neutral traversal. Limits apply between calls, never interrupt a COM call.</summary>
public static class BoundedSearch
{
    public static SearchResult<T> Find<T>(
        IEnumerable<T> roots,
        Func<T, IEnumerable<T>> children,
        Func<T, bool> matches,
        Func<T, string?> identity,
        SearchBudget budget,
        int depthLimit,
        int resultLimit
    )
    {
        var queue = new Queue<(T Node, int Depth)>();
        var result = new List<(T Node, int Depth)>();
        var seen = new HashSet<string>();
        var truncated = false;
        var unreadable = 0;
        foreach (var root in roots)
        {
            OperationContext.Check();
            if (!budget.Available || queue.Count >= budget.Remaining)
            {
                truncated = true;
                break;
            }

            queue.Enqueue((root, 0));
        }

        while (queue.Count > 0)
        {
            OperationContext.Check();
            if (!budget.Available)
            {
                truncated = true;
                break;
            }

            var item = queue.Dequeue();
            budget.Visit();
            try
            {
                var key = identity(item.Node);
                if (key != null && !seen.Add(key))
                {
                    continue;
                }

                if (matches(item.Node))
                {
                    result.Add(item);
                    if (result.Count >= resultLimit)
                    {
                        truncated = true;
                        break;
                    }
                }
                // A leaf at the depth limit is complete. Probe for a child without
                // descending so truncation represents genuinely omitted content.
                OperationContext.Check();
                using var iterator = children(item.Node).GetEnumerator();
                while (true)
                {
                    OperationContext.Check();
                    if (!budget.Available)
                    {
                        truncated = true;
                        break;
                    }

                    if (!iterator.MoveNext())
                    {
                        break;
                    }

                    if (item.Depth >= depthLimit || queue.Count >= budget.Remaining)
                    {
                        truncated = true;
                        break;
                    }

                    queue.Enqueue((iterator.Current, item.Depth + 1));
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch
            {
                unreadable++;
            }
        }

        return new(result, truncated, unreadable);
    }
}
