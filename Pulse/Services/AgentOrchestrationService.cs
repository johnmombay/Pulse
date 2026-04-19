using Pulse.Hubs;
using Pulse.Models;
using Microsoft.AspNetCore.SignalR;
using Microsoft.SemanticKernel;
using Microsoft.SemanticKernel.ChatCompletion;
using Microsoft.SemanticKernel.Connectors.Google;
using ModelContextProtocol.Client;
using System.Text;

namespace Pulse.Services;

/// <summary>
/// Core agent orchestration: reads live settings from LlmSettingsService, loads
/// enabled MCP servers as Semantic Kernel plugins, then streams the Gemini response
/// back to connected clients via SignalR.
/// </summary>
public sealed class AgentOrchestrationService(
    GeminiKeyRotationService keyRotation,
    ChatHistoryService chatHistory,
    LlmSettingsService llmSettings,
    McpService mcpService,
    RagService ragService,
    MemoryService memoryService,
    DatabaseToolsPlugin databaseToolsPlugin,
    PdfGeneratorPlugin pdfGeneratorPlugin,
    WordGeneratorPlugin wordGeneratorPlugin,
    ExcelGeneratorPlugin excelGeneratorPlugin,
    TerminalPlugin terminalPlugin,
    AgentMailPlugin agentMailPlugin,
    FlatFileDataPlugin flatFileDataPlugin,
    IHubContext<AgentHub> hubContext,
    ILogger<AgentOrchestrationService> logger)
{
    /// <summary>
    /// Executes one user-message turn and returns the full assistant response text
    /// (used by the caller to queue a memory-extraction job).
    /// </summary>
    public async Task<string> ExecuteAsync(
        string sessionId,
        string userMessage,
        string userId,
        CancellationToken cancellationToken = default)
    {
        var sessionLock = chatHistory.GetSessionLock(sessionId);
        await sessionLock.WaitAsync(cancellationToken);

        McpClient[] mcpClients = [];
        try
        {
            var skHistory = chatHistory.GetSkHistory(sessionId);

            await hubContext.Clients.Group(sessionId)
                .SendAsync("AgentStatus", "thinking", cancellationToken: cancellationToken);
            chatHistory.SetSessionStatus(sessionId, "thinking");

            // Read live settings — picks up any changes saved on the Settings page
            var settings = llmSettings.Get();

            // On the first user message of a new session, inject persistent memories
            // and active skill instructions as system messages.
            if (skHistory.Count == 1)
            {
                // ── Persistent memory ────────────────────────────────────────
                if (!string.IsNullOrWhiteSpace(userId))
                {
                    var memories = await memoryService.GetSessionContextAsync(userId, cancellationToken);
                    if (memories.Count > 0)
                    {
                        var memLines = string.Join("\n", memories.Select(m => $"- {m.Content}"));
                        skHistory.AddSystemMessage(
                            $"Known facts and preferences about this user " +
                            $"(use these to personalise responses):\n{memLines}");

                        logger.LogInformation(
                            "Session {SessionId}: injected {N} memory item(s) for user {UserId}",
                            sessionId, memories.Count, userId);
                    }
                }

                // ── Active skills ─────────────────────────────────────────────
                var activeSkills = (settings.Skills ?? []).Where(s => s.IsActive).ToList();
                if (activeSkills.Count > 0)
                {
                    var skillsText = string.Join("\n\n---\n\n",
                        activeSkills.Select(s => $"## {s.Icon} {s.Name}\n{s.Instructions}"));
                    skHistory.AddSystemMessage(
                        $"Apply the following skill instructions throughout this conversation:\n\n{skillsText}");

                    logger.LogInformation(
                        "Session {SessionId}: injected {Count} active skill(s): {Names}",
                        sessionId, activeSkills.Count,
                        string.Join(", ", activeSkills.Select(s => s.Name)));
                }
            }

            // ── RAG: retrieve relevant chunks and augment the user message ─────
            string messageToSend = userMessage;
            var enabledRag = (settings.RagDocuments ?? [])
                .Where(d => d.IsEnabled && d.ChunkCount > 0).ToList();

            if (enabledRag.Count > 0)
            {
                var chunks = await ragService.RetrieveAsync(userMessage, cancellationToken);
                if (chunks.Count > 0)
                {
                    var ctx = string.Join("\n\n---\n\n", chunks);
                    messageToSend =
                        $"<knowledge_base_context>\n{ctx}\n</knowledge_base_context>\n\n" +
                        $"Using ONLY the knowledge base context above when it is relevant, " +
                        $"answer the following question. If the context contains the answer, " +
                        $"cite it directly. Do not claim you lack the information if it appears above.\n\n" +
                        $"Question: {userMessage}";

                    logger.LogInformation(
                        "Session {SessionId}: augmented message with {N} RAG chunk(s)",
                        sessionId, chunks.Count);
                }
            }

            skHistory.AddUserMessage(messageToSend);
            chatHistory.AddDisplayMessage(sessionId, userId, "user", userMessage);
            await keyRotation.EnforceRateLimitAsync(cancellationToken);
            var apiKey = keyRotation.GetNextKey(settings.ApiKeys);

            var kernelBuilder = Kernel.CreateBuilder()
                .AddGoogleAIGeminiChatCompletion(settings.ModelId, apiKey);

            var kernel = kernelBuilder.Build();

            // Load enabled MCP server tools into the kernel
            var enabledMcp = settings.McpServers.Where(s => s.IsEnabled).ToList();
            if (enabledMcp.Count > 0)
            {
                var (mcpPlugins, clients) = await mcpService.CreatePluginsAsync(
                    enabledMcp, cancellationToken);
                mcpClients = clients;

                foreach (var plugin in mcpPlugins)
                    kernel.Plugins.Add(plugin);

                logger.LogInformation(
                    "Session {SessionId}: loaded {Count} MCP plugin(s)", sessionId, mcpPlugins.Length);
            }

            // Load in-process database tools when connections are configured
            var dbConnections = settings.DatabaseConnections ?? [];
            var enabledDbConns = dbConnections.Where(kvp => kvp.Value.IsEnabled).ToList();
            if (enabledDbConns.Count > 0)
            {
                kernel.Plugins.AddFromObject(databaseToolsPlugin, "Database");
                logger.LogInformation(
                    "Session {SessionId}: loaded DatabaseTools plugin ({Count} connection(s): {Names})",
                    sessionId, enabledDbConns.Count, string.Join(", ", enabledDbConns.Select(c => c.Key)));
            }

            // PDF, Word, Excel generators are always available
            kernel.Plugins.AddFromObject(pdfGeneratorPlugin,   "PdfGenerator");
            kernel.Plugins.AddFromObject(wordGeneratorPlugin,  "WordGenerator");
            kernel.Plugins.AddFromObject(excelGeneratorPlugin, "ExcelGenerator");

            // Terminal plugin — only loaded when explicitly enabled in Settings
            if (settings.TerminalSettings?.IsEnabled == true)
            {
                kernel.Plugins.AddFromObject(terminalPlugin, "Terminal");
                logger.LogInformation("Session {SessionId}: Terminal plugin loaded (shell={Shell})",
                    sessionId, settings.TerminalSettings.DefaultShell);
            }

            // AgentMail plugin — only loaded when enabled in Settings
            if (settings.AgentMail?.IsEnabled == true &&
                !string.IsNullOrWhiteSpace(settings.AgentMail.ApiKey))
            {
                kernel.Plugins.AddFromObject(agentMailPlugin, "AgentMail");
                logger.LogInformation("Session {SessionId}: AgentMail plugin loaded (inbox={Inbox})",
                    sessionId, settings.AgentMail.DefaultInbox);
            }

            // Flat-file data sources — loaded whenever at least one is enabled
            var enabledFlatFiles = (settings.FlatFileSources ?? []).Where(s => s.IsEnabled).ToList();
            if (enabledFlatFiles.Count > 0)
            {
                kernel.Plugins.AddFromObject(flatFileDataPlugin, "FlatFileData");
                logger.LogInformation(
                    "Session {SessionId}: FlatFileData plugin loaded ({Count} source(s): {Names})",
                    sessionId, enabledFlatFiles.Count,
                    string.Join(", ", enabledFlatFiles.Select(s => s.Id)));
            }

            // Chart generator is created per-execution so it has access to sessionId + hub
            var chartPlugin = new ChartGeneratorPlugin(sessionId, userId, hubContext, chatHistory, logger);
            kernel.Plugins.AddFromObject(chartPlugin, "ChartGenerator");

            var chatService = kernel.GetRequiredService<IChatCompletionService>();

            // Enable auto-invocation of MCP tools when plugins are present
            var executionSettings = new GeminiPromptExecutionSettings
            {
                MaxTokens = 8192,
                Temperature = 0.7,
                ToolCallBehavior = kernel.Plugins.Count > 0
                    ? GeminiToolCallBehavior.AutoInvokeKernelFunctions
                    : null
            };

            await hubContext.Clients.Group(sessionId)
                .SendAsync("AgentStatus", "responding", cancellationToken: cancellationToken);
            chatHistory.SetSessionStatus(sessionId, "responding");

            var responseBuilder = new StringBuilder();

            await foreach (var chunk in chatService.GetStreamingChatMessageContentsAsync(
                skHistory,
                executionSettings: executionSettings,
                kernel: kernel,
                cancellationToken: cancellationToken))
            {
                if (!string.IsNullOrEmpty(chunk.Content))
                {
                    responseBuilder.Append(chunk.Content);
                    await hubContext.Clients.Group(sessionId)
                        .SendAsync("ReceiveChunk", chunk.Content, cancellationToken: cancellationToken);
                }
            }

            var fullResponse = responseBuilder.ToString();

            if (!string.IsNullOrEmpty(fullResponse))
            {
                skHistory.AddAssistantMessage(fullResponse);
                chatHistory.AddDisplayMessage(sessionId, userId, "assistant", fullResponse);
            }

            var awaitingInput = fullResponse.Contains("[INPUT NEEDED]", StringComparison.OrdinalIgnoreCase);

            await hubContext.Clients.Group(sessionId)
                .SendAsync("TaskCompleted", new { sessionId, awaitingInput }, cancellationToken: cancellationToken);

            logger.LogInformation(
                "Agent task completed for session {SessionId}. Model={Model} MCP={McpCount} AwaitingInput={AwaitingInput}",
                sessionId, settings.ModelId, mcpClients.Length, awaitingInput);

            return fullResponse;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Agent execution failed for session {SessionId}", sessionId);
            await hubContext.Clients.Group(sessionId)
                .SendAsync("TaskError", ex.Message, cancellationToken: cancellationToken);
            return "";
        }
        finally
        {
            chatHistory.ClearSessionStatus(sessionId);

            // Dispose MCP client connections (closes SSE streams / kills stdio processes)
            foreach (var client in mcpClients)
                await client.DisposeAsync();

            // Guard against ObjectDisposedException: the session (and its SemaphoreSlim)
            // can be deleted by RemoveSession() while the agent is still executing.
            try { sessionLock.Release(); }
            catch (ObjectDisposedException) { /* session was deleted during execution */ }
        }
    }
}
