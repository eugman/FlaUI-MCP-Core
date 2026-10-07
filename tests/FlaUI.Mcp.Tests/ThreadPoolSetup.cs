using System.Runtime.CompilerServices;

namespace FlaUI.Mcp.Tests;

/// <summary>
/// Several tests block a thread on purpose (a synchronous tool body, a hung provider). On a machine
/// that reports few processors the pool then grows too slowly and timing tests fail at random, so
/// start it with enough threads for the parallel test run.
/// </summary>
internal static class ThreadPoolSetup
{
    [ModuleInitializer]
    internal static void RaiseMinimumThreads() => ThreadPool.SetMinThreads(32, 32);
}
