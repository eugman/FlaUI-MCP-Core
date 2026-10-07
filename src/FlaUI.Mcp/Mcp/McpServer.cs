using System.Text.Json;

namespace FlaUI.Mcp;

/// <summary>
/// MCP Server that handles JSON-RPC over stdio
/// </summary>
public class McpServer
{
    private readonly ToolRegistry _toolRegistry;
    private readonly string _serverName;
    private readonly bool _concurrentRequests;

    public McpServer(ToolRegistry toolRegistry, string serverName = "flaui-mcp", bool concurrentRequests = false)
    {
        _toolRegistry = toolRegistry;
        _serverName = serverName;
        _concurrentRequests = concurrentRequests;
    }

    public async Task RunAsync(CancellationToken cancellationToken = default)
    {
        using var stdin = Console.OpenStandardInput();
        using var stdout = Console.OpenStandardOutput();
        using var reader = new StreamReader(stdin);
        using var writer = new StreamWriter(stdout) { AutoFlush = true };
        using var outputGate = new SemaphoreSlim(1, 1);
        var requests = new List<Task>();
        async Task Respond(JsonRpcRequest request)
        {
            try
            {
                var response = await HandleRequestAsync(request);
                if (response == null)
                {
                    return;
                }

                await outputGate.WaitAsync();
                try
                {
                    await writer.WriteLineAsync(JsonSerializer.Serialize(response, McpProtocol.JsonOptions));
                }
                finally
                {
                    outputGate.Release();
                }
            }
            catch (Exception error)
            {
                Console.Error.WriteLine(error.Message);
            }
        }

        // Redirect stderr for logging (MCP servers should not write to stdout except JSON-RPC)
        Console.SetError(new StreamWriter(Console.OpenStandardError()) { AutoFlush = true });

        while (!cancellationToken.IsCancellationRequested)
        {
            var line = await reader.ReadLineAsync(cancellationToken);
            if (line == null)
            {
                break;
            }

            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            try
            {
                var request = JsonSerializer.Deserialize<JsonRpcRequest>(line, McpProtocol.JsonOptions);
                if (request == null)
                {
                    continue;
                }

                if (_concurrentRequests)
                {
                    requests.RemoveAll(t => t.IsCompleted);
                    if (requests.Count >= 32)
                    {
                        await Task.WhenAny(requests);
                    }

                    requests.Add(Task.Run(() => Respond(request)));
                }
                else
                {
                    await Respond(request);
                }
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"Error processing request: {ex.Message}");
            }
        }

        await Task.WhenAll(requests);
    }

    internal async Task<JsonRpcResponse?> HandleRequestAsync(JsonRpcRequest request)
    {
        try
        {
            object? result = request.Method switch
            {
                "initialize" => HandleInitialize(request),
                "notifications/initialized" => null, // No response for notifications
                "tools/list" => HandleToolsList(),
                "tools/call" => await HandleToolCallAsync(request),
                _ => throw new Exception($"Unknown method: {request.Method}"),
            };

            if (result == null)
            {
                return null; // Notification, no response
            }

            return new JsonRpcResponse { Id = request.Id, Result = result };
        }
        catch (Exception ex)
        {
            return new JsonRpcResponse
            {
                Id = request.Id,
                Error = new JsonRpcError { Code = -32603, Message = ex.Message },
            };
        }
    }

    private McpInitializeResult HandleInitialize(JsonRpcRequest request)
    {
        return new McpInitializeResult
        {
            ProtocolVersion = "2024-11-05",
            Capabilities = new McpCapabilities { Tools = new ToolsCapability { ListChanged = false } },
            ServerInfo = new McpServerInfo { Name = _serverName, Version = "0.1.0" },
        };
    }

    private McpToolsListResult HandleToolsList()
    {
        return new McpToolsListResult { Tools = _toolRegistry.GetToolDefinitions() };
    }

    private async Task<McpToolResult> HandleToolCallAsync(JsonRpcRequest request)
    {
        if (request.Params == null)
        {
            return ErrorResult("Missing params");
        }

        var callParams = JsonSerializer.Deserialize<McpToolCallParams>(
            request.Params.Value.GetRawText(),
            McpProtocol.JsonOptions
        );

        if (callParams == null)
        {
            return ErrorResult("Invalid tool call params");
        }

        return await _toolRegistry.ExecuteToolAsync(callParams.Name, callParams.Arguments);
    }

    private static McpToolResult ErrorResult(string message) =>
        new()
        {
            Content = new List<McpContent>
            {
                new() { Type = "text", Text = message },
            },
            IsError = true,
        };
}
