using Pulse.Models;
using Microsoft.Extensions.AI;
using Microsoft.SemanticKernel;
using ModelContextProtocol.Client;
using System.Text.RegularExpressions;

namespace Pulse.Services;

/// <summary>
/// Transient service that connects to configured MCP servers and returns
/// their tools as Semantic Kernel KernelPlugins ready for auto-invocation.
/// Callers are responsible for disposing the returned McpClient array after use.
/// </summary>
public sealed class McpService(ILoggerFactory loggerFactory, ILogger<McpService> logger)
{
    /// <summary>
    /// Connects to every enabled MCP server in <paramref name="configs"/>,
    /// lists their tools, and wraps each tool as a <see cref="KernelPlugin"/>.
    /// Unreachable servers are logged and skipped rather than throwing.
    /// </summary>
    public async Task<(KernelPlugin[] Plugins, McpClient[] Clients)> CreatePluginsAsync(
        IEnumerable<McpServerConfig> configs,
        CancellationToken cancellationToken = default)
    {
        var clients = new List<McpClient>();
        var plugins = new List<KernelPlugin>();

        foreach (var config in configs)
        {
            try
            {
                var client = await ConnectAsync(config, cancellationToken);
                clients.Add(client);

                var tools = await client.ListToolsAsync(cancellationToken: cancellationToken);
                var functions = tools.Select(WrapAsTool).ToList();

                if (functions.Count > 0)
                    plugins.Add(KernelPluginFactory.CreateFromFunctions(
                        SanitizeName(config.Name), config.Name, functions));

                logger.LogInformation(
                    "MCP server '{Name}' connected — {Count} tool(s) loaded",
                    config.Name, functions.Count);
            }
            catch (Exception ex)
            {
                logger.LogError(ex,
                    "Failed to load MCP server '{Name}' — server will be skipped this call",
                    config.Name);
            }
        }

        return ([.. plugins], [.. clients]);
    }

    // ── Private helpers ───────────────────────────────────────────────────────

    private async Task<McpClient> ConnectAsync(McpServerConfig config, CancellationToken ct)
    {
        IClientTransport transport = config.TransportType.Equals(
            "stdio", StringComparison.OrdinalIgnoreCase)
            ? new StdioClientTransport(
                new StdioClientTransportOptions
                {
                    Command = config.Command
                        ?? throw new InvalidOperationException(
                            $"MCP server '{config.Name}' (stdio) is missing Command."),
                    Arguments = config.Arguments?
                        .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                        .ToList(),
                    Name = config.Name
                },
                loggerFactory)
            : new HttpClientTransport(
                new HttpClientTransportOptions
                {
                    Endpoint = new Uri(config.Url
                        ?? throw new InvalidOperationException(
                            $"MCP server '{config.Name}' (http) is missing URL.")),
                    Name = config.Name
                },
                loggerFactory: loggerFactory);

        return await McpClient.CreateAsync(
            transport,
            new McpClientOptions(),
            loggerFactory,
            ct);
    }

    /// <summary>
    /// Bridges an MCP <see cref="McpClientTool"/> (which is an
    /// <see cref="AIFunction"/>) into a Semantic Kernel <see cref="KernelFunction"/>.
    /// </summary>
    private static KernelFunction WrapAsTool(McpClientTool tool)
    {
        return KernelFunctionFactory.CreateFromMethod(
            async (KernelArguments args, CancellationToken ct) =>
            {
                var callArgs = args
                    .Where(kv => kv.Value is not null)
                    .ToDictionary(kv => kv.Key, kv => kv.Value);

                var result = await tool.InvokeAsync(new AIFunctionArguments(callArgs), ct);
                return result?.ToString() ?? string.Empty;
            },
            functionName: SanitizeName(tool.Name),
            description: tool.Description ?? tool.Name);
    }

    private static string SanitizeName(string name)
    {
        var clean = Regex.Replace(name, @"[^a-zA-Z0-9_]", "_").TrimStart('_');
        return clean.Length > 0 ? clean : "tool";
    }
}
