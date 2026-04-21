using Microsoft.SemanticKernel;
using Microsoft.SemanticKernel.ChatCompletion;
using Microsoft.SemanticKernel.Connectors.Google;
using Pulse.Infrastructure;

namespace Pulse.Services;

/// <summary>
/// Runs one agent turn silently — no SignalR streaming, no chat history.
/// Used by <see cref="Jobs.ScheduledTaskJob"/> so scheduled tasks can invoke
/// the full plugin stack (database, terminal, AgentMail, etc.) from a background job.
/// </summary>
public sealed class ScheduledAgentRunner(
    GeminiKeyRotationService keyRotation,
    LlmSettingsService llmSettings,
    ITenantContext tenantContext,
    DatabaseToolsPlugin databaseToolsPlugin,
    PdfGeneratorPlugin pdfGeneratorPlugin,
    WordGeneratorPlugin wordGeneratorPlugin,
    ExcelGeneratorPlugin excelGeneratorPlugin,
    TerminalPlugin terminalPlugin,
    AgentMailPlugin agentMailPlugin,
    FlatFileDataPlugin flatFileDataPlugin,
    LlmUsageService usageService,
    ILogger<ScheduledAgentRunner> logger)
{
    public async Task<(string Result, bool Success)> RunAsync(
        string instructions, CancellationToken ct = default)
    {
        try
        {
            var settings = await llmSettings.GetAsync(tenantContext.TenantId ?? Guid.Empty);
            await keyRotation.EnforceRateLimitAsync(ct);
            var apiKey = keyRotation.GetNextKey(settings.ApiKeys);

            var kernel = Kernel.CreateBuilder()
                .AddGoogleAIGeminiChatCompletion(settings.ModelId, apiKey)
                .Build();

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

            // Chart capture — no SignalR in scheduled runs; specs are embedded in the output
            var chartPlugin = new ScheduledChartPlugin();
            kernel.Plugins.AddFromObject(chartPlugin, "ChartGenerator");

            var history = new ChatHistory();
            history.AddSystemMessage(
                "You are an autonomous scheduled task agent. " +
                "Execute the given instructions completely and return a clear, " +
                "concise summary of what was done and the results. " +
                "Be specific — include numbers, names, and key findings.");
            history.AddUserMessage(instructions);

            var executionSettings = new GeminiPromptExecutionSettings
            {
                MaxTokens        = 4096,
                Temperature      = 0.3,
                ToolCallBehavior = kernel.Plugins.Count > 0
                    ? GeminiToolCallBehavior.AutoInvokeKernelFunctions
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
    }
}
