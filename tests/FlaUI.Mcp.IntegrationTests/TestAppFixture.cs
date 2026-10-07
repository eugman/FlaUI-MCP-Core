using System.Diagnostics;
using System.Text.Json;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Definitions;
using FlaUI.Mcp;
using FlaUI.Mcp.Core;
using FlaUI.Mcp.Tools;

namespace FlaUI.Mcp.IntegrationTests;

/// <summary>
/// Shared fixture that launches the WinForms and WPF test apps once per test collection.
/// Provides SessionManager and ElementRegistry for all tests to share.
/// Implements IAsyncLifetime for proper async setup/teardown.
/// </summary>
public class TestAppFixture : IAsyncLifetime
{
    // Match the production host before constructing UIA services or reading
    // coordinates. Testhost otherwise mixes UIA physical and virtualized pixels.
    static TestAppFixture() => DpiUtility.EnablePerMonitorV2();

    private const int WindowPollIntervalMs = 250;
    private const int WindowPollTimeoutMs = 15000;

    public SessionManager Session { get; } = new();
    public ElementRegistry Elements { get; } = new();

    public string WinFormsHandle { get; private set; } = "";
    public string WpfHandle { get; private set; } = "";

    private Process? _winFormsProcess;
    private Process? _wpfProcess;

    public async Task InitializeAsync()
    {
        var winFormsPath =
            FindTestAppPath("WinFormsTestApp")
            ?? throw new Exception(
                "WinFormsTestApp not found. Build it first: dotnet build tests/TestApps/WinFormsTestApp"
            );
        var wpfPath =
            FindTestAppPath("WpfTestApp")
            ?? throw new Exception("WpfTestApp not found. Build it first: dotnet build tests/TestApps/WpfTestApp");

        _winFormsProcess = LaunchTestApp(winFormsPath);
        _wpfProcess = LaunchTestApp(wpfPath);
        var winFormsProcessId = _winFormsProcess.Id;
        var wpfProcessId = _wpfProcess.Id;

        // Poll for both windows to appear
        var sw = Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < WindowPollTimeoutMs && (WinFormsHandle == "" || WpfHandle == ""))
        {
            await Task.Delay(WindowPollIntervalMs);

            // Discover only the processes we launched through Win32. Desktop-wide
            // UIA enumeration can block on an unrelated application's provider.
            foreach (var w in Win32Desktop.GetTopLevelWindows(winFormsProcessId))
            {
                if (w.Title == "FlaUI-MCP Test App" && WinFormsHandle == "")
                {
                    WinFormsHandle = Session.RegisterNativeWindow(w.Hwnd, winFormsProcessId);
                }
            }

            foreach (var w in Win32Desktop.GetTopLevelWindows(wpfProcessId))
            {
                if (w.Title == "FlaUI-MCP WPF Test App" && WpfHandle == "")
                {
                    WpfHandle = Session.RegisterNativeWindow(w.Hwnd, wpfProcessId);
                }
            }
        }

        if (WinFormsHandle == "")
        {
            throw new Exception("Timed out waiting for WinForms test app window.");
        }

        if (WpfHandle == "")
        {
            throw new Exception("Timed out waiting for WPF test app window.");
        }
    }

    public Task DisposeAsync()
    {
        CloseProcess(_winFormsProcess);
        CloseProcess(_wpfProcess);
        Session.Dispose();
        return Task.CompletedTask;
    }

    private static Process LaunchTestApp(string path)
    {
        var psi = new ProcessStartInfo(path) { UseShellExecute = true };
        return Process.Start(psi) ?? throw new InvalidOperationException($"Failed to launch test app: {path}");
    }

    private static string? FindTestAppPath(string appName)
    {
        var baseDir = AppContext.BaseDirectory;

        // Walk up to find the repo root (contains src/)
        var dir = new DirectoryInfo(baseDir);
        while (dir != null && !Directory.Exists(Path.Combine(dir.FullName, "src")))
        {
            dir = dir.Parent;
        }

        if (dir == null)
        {
            return null;
        }

        var searchPaths = new[]
        {
            Path.Combine(
                dir.FullName,
                "tests",
                "TestApps",
                appName,
                "bin",
                typeof(TestAppFixture)
                    .Assembly.GetCustomAttributes(typeof(System.Reflection.AssemblyConfigurationAttribute), false)
                    .Cast<System.Reflection.AssemblyConfigurationAttribute>()
                    .Single()
                    .Configuration,
                "net8.0-windows",
                $"{appName}.exe"
            ),
            Path.Combine(
                dir.FullName,
                "tests",
                "TestApps",
                appName,
                "bin",
                "Debug",
                "net8.0-windows",
                $"{appName}.exe"
            ),
            Path.Combine(
                dir.FullName,
                "tests",
                "TestApps",
                appName,
                "bin",
                "Release",
                "net8.0-windows",
                $"{appName}.exe"
            ),
        };

        return searchPaths.FirstOrDefault(File.Exists);
    }

    public Window? GetWinFormsWindow() => Session.GetWindow(WinFormsHandle);

    public Window? GetWpfWindow() => Session.GetWindow(WpfHandle);

    /// <summary>
    /// Call an MCP tool with the given arguments and return the text result.
    /// Shared helper to avoid duplication across test classes.
    /// </summary>
    public async Task<string> CallTool(ToolBase tool, object args)
    {
        var json = JsonSerializer.Serialize(args, McpProtocol.JsonOptions);
        var element = JsonSerializer.Deserialize<JsonElement>(json);
        var result = await tool.ExecuteAsync(element);
        return result.Content.FirstOrDefault()?.Text ?? "";
    }

    /// <summary>
    /// Take a snapshot of a window and return the text.
    /// </summary>
    public string TakeSnapshot(string handle)
    {
        var builder = new SnapshotBuilder(Elements);
        var window = Session.GetWindow(handle)!;
        return builder.BuildSnapshot(handle, window);
    }

    /// <summary>
    /// Find an element ref by name in the snapshot of the given window.
    /// Takes a fresh snapshot each time — use the overload accepting a
    /// pre-built snapshot when multiple lookups are needed.
    /// </summary>
    public string? FindRefByName(string handle, string name)
    {
        var snapshot = TakeSnapshot(handle);
        return FindRefInSnapshot(snapshot, name);
    }

    /// <summary>
    /// Find an element ref by name in a pre-built snapshot string.
    /// Use this when making multiple lookups against the same snapshot.
    /// </summary>
    public static string? FindRefInSnapshot(string snapshot, string name)
    {
        foreach (var line in snapshot.Split('\n'))
        {
            if (line.Contains($"\"{name}\"") && line.Contains("[ref="))
            {
                var refStart = line.IndexOf("[ref=") + 5;
                var refEnd = line.IndexOf("]", refStart);
                return line[refStart..refEnd];
            }
        }

        return null;
    }

    private static void CloseProcess(Process? process)
    {
        if (process == null || process.HasExited)
        {
            return;
        }

        try
        {
            process.CloseMainWindow();
            if (!process.WaitForExit(3000))
            {
                process.Kill();
            }
        }
        catch
        {
            try
            {
                process.Kill();
            }
            catch { }
        }
    }
}

/// <summary>
/// Collection definition so all test classes share the same fixture (same app instances).
/// </summary>
[CollectionDefinition("TestApps")]
public class TestAppCollection : ICollectionFixture<TestAppFixture> { }
