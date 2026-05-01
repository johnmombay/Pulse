using Microsoft.SemanticKernel;
using Microsoft.SemanticKernel.ChatCompletion;
using Microsoft.SemanticKernel.Connectors.OpenAI;
using ModelContextProtocol.Client;
using Pulse.Infrastructure;

namespace Pulse.Services;

/// <summary>
/// Runs one agent turn silently — no SignalR streaming, no chat history.
/// Used by <see cref="Jobs.ScheduledTaskJob"/> so scheduled tasks can invoke
/// the full plugin stack (database, terminal, AgentMail, etc.) from a background job.
/// </summary>
public sealed class ScheduledAgentRunner(
    OpenRouterService openRouter,
    LlmSettingsService llmSettings,
    ITenantContext tenantContext,
    DatabaseToolsPlugin databaseToolsPlugin,
    PdfGeneratorPlugin pdfGeneratorPlugin,
    WordGeneratorPlugin wordGeneratorPlugin,
    ExcelGeneratorPlugin excelGeneratorPlugin,
    TerminalPlugin terminalPlugin,
    AgentMailPlugin agentMailPlugin,
    FlatFileDataPlugin flatFileDataPlugin,
    WebSearchPlugin webSearchPlugin,
    McpService mcpService,
    LlmUsageService usageService,
    AgentReflectionService reflectionService,
    ILogger<ScheduledAgentRunner> logger)
{
    public async Task<(string Result, bool Success)> RunAsync(
        string instructions, CancellationToken ct = default)
    {
        McpClient[]? mcpClients = null;
        try
        {
            var settings = await llmSettings.GetAsync(tenantContext.TenantId ?? Guid.Empty);
            var apiKey = openRouter.GetApiKey(settings.ApiKeys);

#pragma warning disable SKEXP0010
            var kernel = Kernel.CreateBuilder()
                .AddOpenAIChatCompletion(settings.ModelId, new Uri(OpenRouterService.BaseUrl), apiKey)
                .Build();
#pragma warning restore SKEXP0010

            // Database tools
            var enabledConns = (settings.DatabaseConnections ?? [])
                .Where(kvp => kvp.Value.IsEnabled).ToList();
            if (enabledConns.Count > 0)
                kernel.Plugins.AddFromObject(databaseToolsPlugin, "Database");

            // Document generation (BuildUrl returns relative path when no HttpContext — acceptable)
            kernel.Plugins.AddFromObject(pdfGeneratorPlugin,   "PdfGenerator");
            kernel.Plugins.AddFromObject(wordGeneratorPlugin,  "WordGenerator");
            kernel.Plugins.AddFromObject(excelGeneratorPlugin, "ExcelGenerator");

            // Terminal
            if (settings.TerminalSettings?.IsEnabled == true)
                kernel.Plugins.AddFromObject(terminalPlugin, "Terminal");

            // AgentMail
            if (settings.AgentMail?.IsEnabled == true &&
                !string.IsNullOrWhiteSpace(settings.AgentMail.ApiKey))
                kernel.Plugins.AddFromObject(agentMailPlugin, "AgentMail");

            // Flat-file data sources
            var enabledFlatFiles = (settings.FlatFileSources ?? []).Where(s => s.IsEnabled).ToList();
            if (enabledFlatFiles.Count > 0)
                kernel.Plugins.AddFromObject(flatFileDataPlugin, "FlatFileData");

            // Web search (DuckDuckGo)
            var webSearchLoaded = settings.WebSearch?.IsEnabled == true;
            if (webSearchLoaded)
                kernel.Plugins.AddFromObject(webSearchPlugin, "WebSearch");

            // MCP servers (includes DuckDuckGo search and any others configured by the tenant)
            var enabledMcp = (settings.McpServers ?? []).Where(s => s.IsEnabled).ToList();
            if (enabledMcp.Count > 0)
            {
                var (mcpPlugins, clients) = await mcpService.CreatePluginsAsync(enabledMcp, ct);
                mcpClients = clients;
                foreach (var plugin in mcpPlugins)
                    kernel.Plugins.Add(plugin);
            }

            // Chart capture
            var chartPlugin = new ScheduledChartPlugin();
            kernel.Plugins.AddFromObject(chartPlugin, "ChartGenerator");

            var history = new ChatHistory();
            var systemPrompt = new System.Text.StringBuilder(
                "You are an autonomous scheduled task agent. " +
                "Execute the given instructions completely and return a clear, " +
                "concise summary of what was done and the results. " +
                "Be specific — include numbers, names, and key findings.");
            if (webSearchLoaded)
                systemPrompt.Append(
                    " You have access to the search_web tool — use it proactively whenever " +
                    "you need current information, news, prices, or any live data. " +
                    "Never say you cannot browse the web; call search_web instead.");
            history.AddSystemMessage(systemPrompt.ToString());

            // Self-learning: inject relevant lessons from past runs
            var lessons = await reflectionService.GetRelevantLessonsAsync(
                instructions, AgentDomain.Scheduler, null, ct);
            var lessonBlock = AgentReflectionService.FormatLessonBlock(lessons);
            if (!string.IsNullOrEmpty(lessonBlock))
                history.AddSystemMessage(lessonBlock);

            history.AddUserMessage(instructions);

            var executionSettings = new OpenAIPromptExecutionSettings
            {
                MaxTokens        = 4096,
                Temperature      = 0.3,
                ToolCallBehavior = kernel.Plugins.Count > 0
                    ? ToolCallBehavior.AutoInvokeKernelFunctions
                    : null
            };

            var chatService = kernel.GetRequiredService<IChatCompletionService>();
            var result      = await chatService.GetChatMessageContentAsync(
                history, executionSettings, kernel, ct);

            var content = result.Content ?? "";

            // Append any charts the agent generated during this run
            content = chartPlugin.EmbedIn(content);

            try
            {
                var (p, c, t) = LlmUsageService.ExtractTokens(result.Metadata);
                await usageService.RecordAsync(
                    tenantContext.TenantId ?? Guid.Empty,
                    userId: string.Empty,
                    agentName: "ScheduledTask",
                    modelId: settings.ModelId,
                    promptTokens: p, completionTokens: c, totalTokens: t, ct);
            }
            catch (Exception uex)
            {
                logger.LogWarning(uex, "ScheduledAgentRunner: failed to record LLM usage");
            }

            logger.LogInformation(
                "ScheduledAgentRunner completed ({Chars} chars)", content.Length);

            return (content, true);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "ScheduledAgentRunner failed");
            return ($"Execution error: {ex.Message}", false);
        }
        finally
        {
            if (mcpClients is not null)
                foreach (var c in mcpClients)
                    await c.DisposeAsync();
        }
    }
}
