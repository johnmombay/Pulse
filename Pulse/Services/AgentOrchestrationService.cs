using Pulse.Hubs;
using Pulse.Infrastructure;
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
///
/// When <see cref="LlmSettingsModel.AgentDefinitions"/> is non-empty the orchestrator
/// runs in <b>multi-agent mode</b>: it loads only its own <c>AllowedPluginKeys</c> and
/// gains one <c>DelegateTo{Name}</c> kernel function per enabled specialist.
/// When the table is empty (fresh install before seeding) it falls back to the legacy
/// all-plugins-loaded monolithic behaviour.
/// </summary>
public sealed class AgentOrchestrationService(
    GeminiKeyRotationService keyRotation,
    ChatHistoryService chatHistory,
    LlmSettingsService llmSettings,
    ITenantContext tenantContext,
    SpecializedAgentRunner specializedRunner,
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
    LlmUsageService usageService,
    IHubContext<AgentHub> hubContext,
    ILogger<AgentOrchestrationService> logger)
{
    private static GoogleAIVersion ParseApiVersion(string? value) =>
        string.Equals(value, "V1", StringComparison.OrdinalIgnoreCase)
            ? GoogleAIVersion.V1
            : GoogleAIVersion.V1_Beta;

    /// <summary>
    /// Returns <c>true</c> for HTTP statuses Gemini emits transiently and that are safe
    /// to retry when no response chunks have been streamed yet:
    /// 429 (quota), 502 (bad gateway), 503 (model overloaded), 504 (gateway timeout).
    /// </summary>
    private static bool IsTransientStatus(System.Net.HttpStatusCode? code) => code is
        System.Net.HttpStatusCode.TooManyRequests or
        System.Net.HttpStatusCode.BadGateway or
        System.Net.HttpStatusCode.ServiceUnavailable or
        System.Net.HttpStatusCode.GatewayTimeout;

    /// <summary>
    /// Pulls Gemini's <c>error.message</c> out of the JSON response body when present
    /// (Google's payload looks like <c>{"error":{"code":400,"message":"...","status":"..."}}</c>).
    /// Falls back to the raw body, then to the exception message.
    /// </summary>
    private static string ExtractGeminiError(HttpOperationException ex)
    {
        var body = ex.ResponseContent;
        if (string.IsNullOrWhiteSpace(body)) return ex.Message;

        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(body);
            if (doc.RootElement.TryGetProperty("error", out var err))
            {
                var msg = err.TryGetProperty("message", out var m) ? m.GetString() : null;
                var status = err.TryGetProperty("status", out var s) ? s.GetString() : null;
                if (!string.IsNullOrWhiteSpace(msg))
                    return string.IsNullOrWhiteSpace(status) ? msg! : $"{msg} ({status})";
            }
        }
        catch (System.Text.Json.JsonException) { /* fall through */ }

        return body.Length > 500 ? body[..500] + "…" : body;
    }

    private static string BuildUserFacingError(Exception ex, string modelId)
    {
        if (ex is InvalidOperationException ioe) return ioe.Message;

        if (ex is HttpOperationException hoe)
        {
            var detail = ExtractGeminiError(hoe);
            var prefix = hoe.StatusCode switch
            {
                System.Net.HttpStatusCode.BadRequest =>
                    $"Gemini rejected the request (400) for model '{modelId}'. " +
                    "Most common cause: the model name is not valid for the selected API version " +
                    "(try toggling Settings \u2192 LLM \u2192 Gemini API Version between v1 and v1beta).",
                System.Net.HttpStatusCode.Unauthorized =>
                    "Gemini rejected the API key (401). Verify the key in Settings \u2192 LLM.",
                System.Net.HttpStatusCode.Forbidden =>
                    $"Gemini denied the request (403) for model '{modelId}'. " +
                    "Either the model is not enabled for your API key's Google project (common for *-preview models), " +
                    "or the key has no access to this model.",
                System.Net.HttpStatusCode.NotFound =>
                    $"Gemini does not recognize model '{modelId}' (404). " +
                    "Check the spelling in Settings \u2192 LLM \u2192 Model ID, or switch the API Version.",
                System.Net.HttpStatusCode.TooManyRequests =>
                    "All Gemini API keys are currently rate-limited. Try again in a minute, or add more keys in Settings \u2192 LLM.",
                _ => $"Gemini returned HTTP {(int?)hoe.StatusCode}."
            };

            return $"{prefix} Details from Google: {detail}";
        }

        return ex.Message;
    }

    /// <summary>
    /// Best-effort parse of Gemini's <c>retryDelay</c> hint
    /// the JSON error payload surfaced on <see cref="HttpOperationException.ResponseContent"/>.
    /// Capped at 5 minutes to avoid pathological waits.
    /// </summary>
    private static TimeSpan? TryParseRetryAfter(HttpOperationException ex)
    {
        var body = ex.ResponseContent;
        if (string.IsNullOrEmpty(body)) return null;

        const string marker = "\"retryDelay\":\"";
        var i = body.IndexOf(marker, StringComparison.Ordinal);
        if (i < 0) return null;

        i += marker.Length;
        var j = body.IndexOf('"', i);
        if (j < 0) return null;

        var token = body[i..j]; // e.g. "42s"
        if (token.EndsWith('s') && double.TryParse(token[..^1], out var secs))
            return TimeSpan.FromSeconds(Math.Min(Math.Max(secs, 1), 300));

        return null;
    }

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
        string modelId = "";
        try
        {
            var skHistory = chatHistory.GetSkHistory(sessionId);

            await hubContext.Clients.Group(sessionId)
                .SendAsync("AgentStatus", "thinking", cancellationToken: cancellationToken);
            chatHistory.SetSessionStatus(sessionId, "thinking");

            var tenantId = tenantContext.TenantId ?? Guid.Empty;
            var settings = await llmSettings.GetAsync(tenantId);

            // ── Agent system determination ────────────────────────────────────
            var agentDefs        = settings.AgentDefinitions ?? [];
            var orchestratorDef  = agentDefs.FirstOrDefault(a => a.IsOrchestrator && a.IsEnabled);
            var enabledSpecialists = agentDefs.Where(a => a.IsEnabled && !a.IsOrchestrator).ToList();
            var useAgentSystem   = agentDefs.Count > 0;

            // Model ID: always use the global LLM Configuration value. Per-agent ModelId
            // overrides on the Orchestrator definition are intentionally ignored so the
            // Settings → LLM → Model ID field is the single source of truth.
            modelId = settings.ModelId;

            // ── First-turn setup ──────────────────────────────────────────────
            if (skHistory.Count == 1)
            {
                // Set orchestrator system prompt when the agent system is active
                if (useAgentSystem && orchestratorDef is not null)
                {
                    chatHistory.SetSessionSystemPrompt(sessionId,
                        BuildOrchestratorPrompt(orchestratorDef, enabledSpecialists));
                }

                // ── Persistent memory (global / orchestrator scope) ───────────
                if (!string.IsNullOrWhiteSpace(userId))
                {
                    var memories = await memoryService.GetSessionContextAsync(userId, null, cancellationToken);
                    if (memories.Count > 0)
                    {
                        var memLines = string.Join("\n", memories.Select(m => $"- {m.Content}"));
                        skHistory.AddSystemMessage(
                            $"Known facts and preferences about this user " +
                            $"(use these to personalise responses):\n{memLines}");

                        logger.LogInformation(
                            "Session {SessionId}: injected {N} global memory item(s) for user {UserId}",
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
                var chunks = await ragService.RetrieveAsync(userMessage, tenantId, cancellationToken);
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

            await hubContext.Clients.Group(sessionId)
                .SendAsync("AgentStatus", "responding", cancellationToken: cancellationToken);
            chatHistory.SetSessionStatus(sessionId, "responding");

            var responseBuilder = new StringBuilder();
            const int maxAttempts = 5;
            Exception? lastError = null;

            for (var attempt = 1; attempt <= maxAttempts; attempt++)
            {
                // Dispose any MCP clients from a previous failed attempt before rebuilding.
                foreach (var c in mcpClients) await c.DisposeAsync();
                mcpClients = [];

                await keyRotation.EnforceRateLimitAsync(cancellationToken);
                var apiKey = keyRotation.GetNextKey(settings.ApiKeys);

                var kernelBuilder = Kernel.CreateBuilder()
                    .AddGoogleAIGeminiChatCompletion(modelId, apiKey, apiVersion: ParseApiVersion(settings.ApiVersion));
                var kernel = kernelBuilder.Build();

                // ── Plugin loading ────────────────────────────────────────────
                if (!useAgentSystem)
                {
                    // Legacy path: all plugins loaded (backwards compat for empty AgentDefinitions)
                    mcpClients = await LoadAllPluginsAsync(kernel, settings, sessionId, userId, cancellationToken);
                }
                else
                {
                    // Orchestrator path: only load plugins listed in orchestrator's AllowedPluginKeys
                    var allowedKeys = orchestratorDef?.AllowedPluginKeys ?? [];
                    mcpClients = await LoadScopedPluginsAsync(kernel, settings, allowedKeys, sessionId, userId,
                        cancellationToken);

                    // Delegation plugin: one DelegateTo{Name} function per specialist
                    if (enabledSpecialists.Count > 0)
                    {
                        var delegationPlugin = AgentDelegationPlugin.Build(
                            enabledSpecialists, specializedRunner, userId, sessionId);
                        kernel.Plugins.Add(delegationPlugin);
                        if (attempt == 1)
                        {
                            logger.LogInformation(
                                "Session {SessionId}: delegation plugin loaded ({Count} specialist(s): {Names})",
                                sessionId, enabledSpecialists.Count,
                                string.Join(", ", enabledSpecialists.Select(s => s.Name)));
                        }
                    }
                }

                var chatService = kernel.GetRequiredService<IChatCompletionService>();

                var executionSettings = new GeminiPromptExecutionSettings
                {
                    MaxTokens = 8192,
                    Temperature = 0.7,
                    ToolCallBehavior = kernel.Plugins.Count > 0
                        ? GeminiToolCallBehavior.AutoInvokeKernelFunctions
                        : null
                };

                responseBuilder.Clear();
                var streamedAnything = false;
                var usage = (Prompt: 0, Completion: 0, Total: 0);

                try
                {
                    await foreach (var chunk in chatService.GetStreamingChatMessageContentsAsync(
                        skHistory,
                        executionSettings: executionSettings,
                        kernel: kernel,
                        cancellationToken: cancellationToken))
                    {
                        usage = LlmUsageService.ExtractTokens(chunk.Metadata, usage);
                        if (!string.IsNullOrEmpty(chunk.Content))
                        {
                            streamedAnything = true;
                            responseBuilder.Append(chunk.Content);
                            await hubContext.Clients.Group(sessionId)
                                .SendAsync("ReceiveChunk", chunk.Content, cancellationToken: cancellationToken);
                        }
                    }

                    // Best-effort — never let usage persistence break the agent turn.
                    try
                    {
                        await usageService.RecordAsync(
                            tenantId, userId, "Orchestrator", modelId,
                            usage.Prompt, usage.Completion, usage.Total, cancellationToken);
                    }
                    catch (Exception uex)
                    {
                        logger.LogWarning(uex, "Session {SessionId}: failed to record LLM usage", sessionId);
                    }

                    lastError = null;
                    break; // success
                }
                catch (HttpOperationException ex) when (
                    IsTransientStatus(ex.StatusCode) &&
                    !streamedAnything &&
                    attempt < maxAttempts)
                {
                    // 429 / 503 / 502 / 504 are transient. Park the key only on 429
                    // (Google quota); for 5xx the model itself is overloaded so a
                    // different key won't help — just back off and retry.
                    if (ex.StatusCode == System.Net.HttpStatusCode.TooManyRequests)
                    {
                        var cooldown = TryParseRetryAfter(ex) ?? TimeSpan.FromSeconds(60);
                        keyRotation.MarkRateLimited(apiKey, cooldown);
                    }

                    // Honor Gemini's retryDelay hint when present, else exponential backoff.
                    var backoff = TryParseRetryAfter(ex)
                                  ?? TimeSpan.FromMilliseconds(750 * Math.Pow(2, attempt - 1));

                    logger.LogWarning(
                        "Session {SessionId}: Gemini {Status} on attempt {Attempt}/{Max}. " +
                        "Retrying after {Backoff}ms.",
                        sessionId, (int)ex.StatusCode!, attempt, maxAttempts, backoff.TotalMilliseconds);

                    await Task.Delay(backoff, cancellationToken);
                    lastError = ex;
                }
                catch (HttpOperationException ex) when (
                    ex.StatusCode == System.Net.HttpStatusCode.NotFound &&
                    !streamedAnything)
                {
                    // 404 = model unknown to the Gemini API (or unavailable to this key's project).
                    // This is not retryable — bail with an actionable message.
                    throw new InvalidOperationException(
                        $"Gemini returned 404 for model '{modelId}'. The model name is invalid, deprecated, " +
                        $"or not available to your API key. Open Settings \u2192 LLM and pick a supported " +
                        $"model (e.g. 'gemini-2.0-flash', 'gemini-2.5-flash', 'gemini-2.5-pro').", ex);
                }
            }

            if (lastError is not null)
                throw lastError;

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
                sessionId, modelId, mcpClients.Length, awaitingInput);

            return fullResponse;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Agent execution failed for session {SessionId}", sessionId);

            var userFacing = BuildUserFacingError(ex, modelId);

            await hubContext.Clients.Group(sessionId)
                .SendAsync("TaskError", userFacing, cancellationToken: cancellationToken);
            return "";
        }
        finally
        {
            chatHistory.ClearSessionStatus(sessionId);

            foreach (var client in mcpClients)
                await client.DisposeAsync();

            try { sessionLock.Release(); }
            catch (ObjectDisposedException) { /* session deleted during execution */ }
        }
    }

    // ── Plugin loading helpers ────────────────────────────────────────────────

    /// <summary>Legacy: loads every globally-enabled plugin (no agent gating).</summary>
    private async Task<McpClient[]> LoadAllPluginsAsync(
        Kernel kernel,
        LlmSettingsModel settings,
        string sessionId,
        string userId,
        CancellationToken ct)
    {
        McpClient[] mcpClients = [];
        var enabledMcp = settings.McpServers.Where(s => s.IsEnabled).ToList();
        if (enabledMcp.Count > 0)
        {
            var (mcpPlugins, clients) = await mcpService.CreatePluginsAsync(enabledMcp, ct);
            mcpClients = clients;
            foreach (var plugin in mcpPlugins) kernel.Plugins.Add(plugin);
            logger.LogInformation("Session {SessionId}: loaded {Count} MCP plugin(s)", sessionId, mcpPlugins.Length);
        }

        var enabledDbConns = (settings.DatabaseConnections ?? []).Where(kvp => kvp.Value.IsEnabled).ToList();
        if (enabledDbConns.Count > 0)
        {
            kernel.Plugins.AddFromObject(databaseToolsPlugin, "Database");
            logger.LogInformation("Session {SessionId}: loaded DatabaseTools plugin ({Count} connection(s))",
                sessionId, enabledDbConns.Count);
        }

        kernel.Plugins.AddFromObject(pdfGeneratorPlugin,   "PdfGenerator");
        kernel.Plugins.AddFromObject(wordGeneratorPlugin,  "WordGenerator");
        kernel.Plugins.AddFromObject(excelGeneratorPlugin, "ExcelGenerator");

        if (settings.TerminalSettings?.IsEnabled == true)
        {
            kernel.Plugins.AddFromObject(terminalPlugin, "Terminal");
            logger.LogInformation("Session {SessionId}: Terminal plugin loaded (shell={Shell})",
                sessionId, settings.TerminalSettings.DefaultShell);
        }

        if (settings.AgentMail?.IsEnabled == true && !string.IsNullOrWhiteSpace(settings.AgentMail.ApiKey))
        {
            kernel.Plugins.AddFromObject(agentMailPlugin, "AgentMail");
            logger.LogInformation("Session {SessionId}: AgentMail plugin loaded", sessionId);
        }

        var enabledFlatFiles = (settings.FlatFileSources ?? []).Where(s => s.IsEnabled).ToList();
        if (enabledFlatFiles.Count > 0)
        {
            kernel.Plugins.AddFromObject(flatFileDataPlugin, "FlatFileData");
            logger.LogInformation("Session {SessionId}: FlatFileData plugin loaded ({Count} source(s))",
                sessionId, enabledFlatFiles.Count);
        }

        var chartPlugin = new ChartGeneratorPlugin(sessionId, userId, hubContext, chatHistory, logger);
        kernel.Plugins.AddFromObject(chartPlugin, "ChartGenerator");
        return mcpClients;
    }

    /// <summary>Orchestrator path: loads only plugins listed in <paramref name="allowedKeys"/>.</summary>
    private async Task<McpClient[]> LoadScopedPluginsAsync(
        Kernel kernel,
        LlmSettingsModel settings,
        IReadOnlyList<string> allowedKeys,
        string sessionId,
        string userId,
        CancellationToken ct)
    {
        McpClient[] mcpClients = [];
        if (allowedKeys.Contains(AgentDefinition.PluginKeys.Mcp))
        {
            var enabledMcp = settings.McpServers.Where(s => s.IsEnabled).ToList();
            if (enabledMcp.Count > 0)
            {
                var (mcpPlugins, clients) = await mcpService.CreatePluginsAsync(enabledMcp, ct);
                mcpClients = clients;
                foreach (var plugin in mcpPlugins) kernel.Plugins.Add(plugin);
                logger.LogInformation("Session {SessionId}: orchestrator loaded {Count} MCP plugin(s)", sessionId, mcpPlugins.Length);
            }
        }

        if (allowedKeys.Contains(AgentDefinition.PluginKeys.Database))
        {
            var enabledDbConns = (settings.DatabaseConnections ?? []).Where(kvp => kvp.Value.IsEnabled).ToList();
            if (enabledDbConns.Count > 0)
            {
                kernel.Plugins.AddFromObject(databaseToolsPlugin, "Database");
                logger.LogInformation("Session {SessionId}: orchestrator loaded Database plugin", sessionId);
            }
        }

        if (allowedKeys.Contains(AgentDefinition.PluginKeys.Pdf))
            kernel.Plugins.AddFromObject(pdfGeneratorPlugin, "PdfGenerator");

        if (allowedKeys.Contains(AgentDefinition.PluginKeys.Word))
            kernel.Plugins.AddFromObject(wordGeneratorPlugin, "WordGenerator");

        if (allowedKeys.Contains(AgentDefinition.PluginKeys.Excel))
            kernel.Plugins.AddFromObject(excelGeneratorPlugin, "ExcelGenerator");

        if (allowedKeys.Contains(AgentDefinition.PluginKeys.Terminal) &&
            settings.TerminalSettings?.IsEnabled == true)
        {
            kernel.Plugins.AddFromObject(terminalPlugin, "Terminal");
            logger.LogInformation("Session {SessionId}: orchestrator loaded Terminal plugin", sessionId);
        }

        if (allowedKeys.Contains(AgentDefinition.PluginKeys.AgentMail) &&
            settings.AgentMail?.IsEnabled == true &&
            !string.IsNullOrWhiteSpace(settings.AgentMail.ApiKey))
        {
            kernel.Plugins.AddFromObject(agentMailPlugin, "AgentMail");
            logger.LogInformation("Session {SessionId}: orchestrator loaded AgentMail plugin", sessionId);
        }

        if (allowedKeys.Contains(AgentDefinition.PluginKeys.FlatFileData))
        {
            var enabledFlatFiles = (settings.FlatFileSources ?? []).Where(s => s.IsEnabled).ToList();
            if (enabledFlatFiles.Count > 0)
            {
                kernel.Plugins.AddFromObject(flatFileDataPlugin, "FlatFileData");
                logger.LogInformation("Session {SessionId}: orchestrator loaded FlatFileData plugin", sessionId);
            }
        }

        if (allowedKeys.Contains(AgentDefinition.PluginKeys.Chart))
        {
            var chartPlugin = new ChartGeneratorPlugin(sessionId, userId, hubContext, chatHistory, logger);
            kernel.Plugins.AddFromObject(chartPlugin, "ChartGenerator");
        }

        return mcpClients;
    }

    // ── System-prompt builder ─────────────────────────────────────────────────

    private static string BuildOrchestratorPrompt(
        AgentDefinition orchestrator,
        IReadOnlyList<AgentDefinition> specialists)
    {
        var sb = new StringBuilder(orchestrator.SystemPrompt);

        if (specialists.Count > 0)
        {
            sb.AppendLine("\n\nAvailable specialist agents you can delegate to:");
            foreach (var s in specialists)
                sb.AppendLine($"- DelegateTo{SanitizeName(s.Name)}: {s.Description}");
            sb.AppendLine(
                "\nWhen delegating, pass a complete, self-contained task description — " +
                "the specialist has no memory of the current conversation.");
        }

        return sb.ToString();
    }

    private static string SanitizeName(string name) =>
        System.Text.RegularExpressions.Regex.Replace(name.Trim(), @"[^a-zA-Z0-9]", "");
}
