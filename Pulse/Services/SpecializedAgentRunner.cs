using Hangfire;
using Pulse.Hubs;
using Pulse.Infrastructure;
using Pulse.Jobs;
using Pulse.Models;
using Microsoft.AspNetCore.SignalR;
using Microsoft.SemanticKernel;
using Microsoft.SemanticKernel.ChatCompletion;
using Microsoft.SemanticKernel.Connectors.Google;
using ModelContextProtocol.Client;
using System.Text;

namespace Pulse.Services;

/// <summary>
/// Executes a single specialized agent turn in its own plugin-scoped Semantic Kernel,
/// streaming output back to the parent session via SignalR <c>SubAgentChunk</c> events.
/// Sub-agents never read or write to <see cref="ChatHistoryService"/> — they are
/// stateless delegates that return their accumulated response text to the orchestrator.
/// </summary>
public sealed class SpecializedAgentRunner(
    GeminiKeyRotationService keyRotation,
    LlmSettingsService llmSettings,
    ITenantContext tenantContext,
    McpService mcpService,
    RagService ragService,
    MemoryService memoryService,
    GlobalAgentMailSettingsService agentMailSettings,
    DatabaseToolsPlugin databaseToolsPlugin,
    PdfGeneratorPlugin pdfGeneratorPlugin,
    WordGeneratorPlugin wordGeneratorPlugin,
    ExcelGeneratorPlugin excelGeneratorPlugin,
    TerminalPlugin terminalPlugin,
    AgentMailPlugin agentMailPlugin,
    FlatFileDataPlugin flatFileDataPlugin,
    ChatHistoryService chatHistory,
    LlmUsageService usageService,
    IHubContext<AgentHub> hubContext,
    ILogger<SpecializedAgentRunner> logger)
{
    /// <summary>
    /// Runs the agent against <paramref name="task"/> and streams its output to the
    /// <paramref name="parentSessionId"/> SignalR group. Returns the full response text.
    /// </summary>
    public async Task<string> RunAsync(
        AgentDefinition agent,
        string task,
        string userId,
        string parentSessionId,
        CancellationToken ct = default)
    {
        McpClient[] mcpClients = [];
        try
        {
            await hubContext.Clients.Group(parentSessionId)
                .SendAsync("AgentStatus", $"delegating:{agent.Name}", cancellationToken: ct);

            var tenantId = tenantContext.TenantId ?? Guid.Empty;
            var settings = await llmSettings.GetAsync(tenantId);
            var agentMail = await agentMailSettings.GetAsync();

            await keyRotation.EnforceRateLimitAsync(ct);
            var apiKey = keyRotation.GetNextKey(settings.ApiKeys);

            // Always use the global LLM Configuration model. Per-agent ModelId overrides
            // are intentionally ignored — Settings → LLM → Model ID is the single source of truth.
            var modelId = settings.ModelId;

            var kernelBuilder = Kernel.CreateBuilder()
                .AddGoogleAIGeminiChatCompletion(modelId, apiKey, apiVersion: ParseApiVersion(settings.ApiVersion));
            var kernel = kernelBuilder.Build();

            var allowedKeys = agent.AllowedPluginKeys ?? [];

            // ── MCP servers (scoped to AllowedMcpServerIds) ───────────────────
            if (allowedKeys.Contains(AgentDefinition.PluginKeys.Mcp))
            {
                var mcpFilter = agent.AllowedMcpServerIds is { Count: > 0 }
                    ? agent.AllowedMcpServerIds
                    : null;
                var scopedMcp = (settings.McpServers ?? [])
                    .Where(s => s.IsEnabled && (mcpFilter == null || mcpFilter.Contains(s.Id)))
                    .ToList();

                if (scopedMcp.Count > 0)
                {
                    var (plugins, clients) = await mcpService.CreatePluginsAsync(scopedMcp, ct);
                    mcpClients = clients;
                    foreach (var p in plugins) kernel.Plugins.Add(p);
                    logger.LogInformation(
                        "SubAgent {Name}: loaded {Count} MCP plugin(s)", agent.Name, plugins.Length);
                }
            }

            // ── Database ──────────────────────────────────────────────────────
            if (allowedKeys.Contains(AgentDefinition.PluginKeys.Database))
            {
                var dbFilter = agent.AllowedDatabaseKeys is { Count: > 0 }
                    ? agent.AllowedDatabaseKeys.ToHashSet(StringComparer.OrdinalIgnoreCase)
                    : null;
                var enabledConns = (settings.DatabaseConnections ?? [])
                    .Where(kvp => kvp.Value.IsEnabled &&
                                  (dbFilter == null || dbFilter.Contains(kvp.Key)))
                    .ToList();

                if (enabledConns.Count > 0)
                {
                    kernel.Plugins.AddFromObject(databaseToolsPlugin, "Database");
                    logger.LogInformation(
                        "SubAgent {Name}: Database plugin loaded ({Count} connection(s))",
                        agent.Name, enabledConns.Count);
                }
            }

            // ── PDF ───────────────────────────────────────────────────────────
            if (allowedKeys.Contains(AgentDefinition.PluginKeys.Pdf))
                kernel.Plugins.AddFromObject(pdfGeneratorPlugin, "PdfGenerator");

            // ── Word ──────────────────────────────────────────────────────────
            if (allowedKeys.Contains(AgentDefinition.PluginKeys.Word))
                kernel.Plugins.AddFromObject(wordGeneratorPlugin, "WordGenerator");

            // ── Excel ─────────────────────────────────────────────────────────
            if (allowedKeys.Contains(AgentDefinition.PluginKeys.Excel))
                kernel.Plugins.AddFromObject(excelGeneratorPlugin, "ExcelGenerator");

            // ── Terminal (global IsEnabled veto applies) ──────────────────────
            if (allowedKeys.Contains(AgentDefinition.PluginKeys.Terminal) &&
                settings.TerminalSettings?.IsEnabled == true)
            {
                kernel.Plugins.AddFromObject(terminalPlugin, "Terminal");
                logger.LogInformation("SubAgent {Name}: Terminal plugin loaded", agent.Name);
            }

            // ── AgentMail (global IsEnabled veto applies) ─────────────────────
            if (allowedKeys.Contains(AgentDefinition.PluginKeys.AgentMail) &&
                agentMail.IsEnabled &&
                !string.IsNullOrWhiteSpace(agentMail.ApiKey))
            {
                kernel.Plugins.AddFromObject(agentMailPlugin, "AgentMail");
                logger.LogInformation("SubAgent {Name}: AgentMail plugin loaded", agent.Name);
            }

            // ── Flat-file data (scoped to AllowedFlatFileIds) ─────────────────
            if (allowedKeys.Contains(AgentDefinition.PluginKeys.FlatFileData))
            {
                var ffFilter = agent.AllowedFlatFileIds is { Count: > 0 }
                    ? agent.AllowedFlatFileIds.ToHashSet()
                    : null;
                var enabledFf = (settings.FlatFileSources ?? [])
                    .Where(s => s.IsEnabled && (ffFilter == null || ffFilter.Contains(s.Id)))
                    .ToList();

                if (enabledFf.Count > 0)
                {
                    kernel.Plugins.AddFromObject(flatFileDataPlugin, "FlatFileData");
                    logger.LogInformation(
                        "SubAgent {Name}: FlatFileData plugin loaded ({Count} source(s))",
                        agent.Name, enabledFf.Count);
                }
            }

            // ── Chart (uses parent sessionId so charts appear in the right chat) ─
            if (allowedKeys.Contains(AgentDefinition.PluginKeys.Chart))
            {
                // Pass the real ChatHistoryService so the chart spec is persisted under
                // the parent session and replays on page reload — same behaviour as the
                // orchestrator path in AgentOrchestrationService.LoadAllPluginsAsync.
                var chartPlugin = new ChartGeneratorPlugin(
                    parentSessionId, userId, hubContext,
                    chatHistory,
                    logger);
                kernel.Plugins.AddFromObject(chartPlugin, "ChartGenerator");
            }

            // ── Build chat history ────────────────────────────────────────────
            var history = new Microsoft.SemanticKernel.ChatCompletion.ChatHistory(agent.SystemPrompt);

            // ── Agent-scoped memories ─────────────────────────────────────────
            if (!string.IsNullOrWhiteSpace(userId))
            {
                var memories = await memoryService.GetSessionContextAsync(userId, agent.Id, ct);
                if (memories.Count > 0)
                {
                    var memLines = string.Join("\n", memories.Select(m => $"- {m.Content}"));
                    history.AddSystemMessage(
                        $"Known facts and preferences about this user:\n{memLines}");
                    logger.LogInformation(
                        "SubAgent {Name}: injected {N} memory item(s)", agent.Name, memories.Count);
                }
            }

            // ── Scoped skills (inject only AllowedSkillIds) ───────────────────
            var skillFilter = agent.AllowedSkillIds is { Count: > 0 }
                ? agent.AllowedSkillIds.ToHashSet()
                : null;
            var agentSkills = (settings.Skills ?? [])
                .Where(s => s.IsActive && (skillFilter == null || skillFilter.Contains(s.Id)))
                .ToList();
            if (agentSkills.Count > 0)
            {
                var skillsText = string.Join("\n\n---\n\n",
                    agentSkills.Select(s => $"## {s.Icon} {s.Name}\n{s.Instructions}"));
                history.AddSystemMessage(
                    $"Apply the following skill instructions throughout this task:\n\n{skillsText}");
            }

            // ── Scoped RAG augmentation ───────────────────────────────────────
            string messageToSend = task;
            if (allowedKeys.Contains(AgentDefinition.PluginKeys.Rag))
            {
                var ragFilter = agent.AllowedRagDocumentIds is { Count: > 0 }
                    ? agent.AllowedRagDocumentIds.ToHashSet()
                    : null;
                var enabledRag = (settings.RagDocuments ?? [])
                    .Where(d => d.IsEnabled && d.ChunkCount > 0 &&
                                (ragFilter == null || ragFilter.Contains(d.Id)))
                    .ToList();

                if (enabledRag.Count > 0)
                {
                    var chunks = await ragService.RetrieveAsync(task, tenantId, ct);
                    if (chunks.Count > 0)
                    {
                        var ctx = string.Join("\n\n---\n\n", chunks);
                        messageToSend =
                            $"<knowledge_base_context>\n{ctx}\n</knowledge_base_context>\n\n" +
                            $"Using the knowledge base context above when relevant, answer: {task}";
                        logger.LogInformation(
                            "SubAgent {Name}: augmented with {N} RAG chunk(s)", agent.Name, chunks.Count);
                    }
                }
            }

            history.AddUserMessage(messageToSend);

            // ── Stream response ───────────────────────────────────────────────
            var chatService = kernel.GetRequiredService<IChatCompletionService>();
            var execSettings = new GeminiPromptExecutionSettings
            {
                MaxTokens = 8192,
                Temperature = 0.7,
                ToolCallBehavior = kernel.Plugins.Count > 0
                    ? GeminiToolCallBehavior.AutoInvokeKernelFunctions
                    : null
            };

            var responseBuilder = new StringBuilder();
            var usage = (Prompt: 0, Completion: 0, Total: 0);

            await foreach (var chunk in chatService.GetStreamingChatMessageContentsAsync(
                history, executionSettings: execSettings, kernel: kernel, cancellationToken: ct))
            {
                usage = LlmUsageService.ExtractTokens(chunk.Metadata, usage);
                if (!string.IsNullOrEmpty(chunk.Content))
                {
                    responseBuilder.Append(chunk.Content);
                    await hubContext.Clients.Group(parentSessionId)
                        .SendAsync("SubAgentChunk", new
                        {
                            agentId   = agent.Id,
                            agentName = agent.Name,
                            icon      = agent.Icon,
                            content   = chunk.Content
                        }, cancellationToken: ct);
                }
            }

            try
            {
                await usageService.RecordAsync(
                    tenantId, userId, agent.Name, modelId,
                    usage.Prompt, usage.Completion, usage.Total, ct);
            }
            catch (Exception uex)
            {
                logger.LogWarning(uex, "SubAgent {Name}: failed to record LLM usage", agent.Name);
            }

            await hubContext.Clients.Group(parentSessionId)
                .SendAsync("AgentStatus", $"delegating-complete:{agent.Name}", cancellationToken: ct);

            var fullResponse = responseBuilder.ToString();
            logger.LogInformation(
                "SubAgent {Name} completed ({Chars} chars)", agent.Name, fullResponse.Length);

            // Enqueue scoped memory extraction — tagged with this agent's id
            if (!string.IsNullOrWhiteSpace(userId) && !string.IsNullOrWhiteSpace(fullResponse))
            {
                BackgroundJob.Enqueue<MemoryExtractionJob>(j =>
                    j.ExtractAsync(tenantId, userId, task, fullResponse, agent.Id, JobCancellationToken.Null));
            }

            return fullResponse;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "SubAgent {Name} failed", agent.Name);
            return $"Sub-agent {agent.Name} failed: {ex.Message}";
        }
        finally
        {
            foreach (var client in mcpClients)
                await client.DisposeAsync();
        }
    }

    private static GoogleAIVersion ParseApiVersion(string? value) =>
        string.Equals(value, "V1", StringComparison.OrdinalIgnoreCase)
            ? GoogleAIVersion.V1
            : GoogleAIVersion.V1_Beta;
}
