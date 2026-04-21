using Microsoft.EntityFrameworkCore;
using Pulse.Data;
using Pulse.Data.Entities;
using Pulse.Models;
using System.Text.Json;

namespace Pulse.Services;

/// <summary>
/// Seeds the default set of <see cref="AgentDefinitionEntity"/> rows for any tenant
/// that currently has none. Safe to call on every startup — no-op once seeded.
/// </summary>
public static class LlmSettingsSeeder
{
    public static async Task SeedAgentDefinitionsAsync(IDbContextFactory<ApplicationDbContext> dbFactory)
    {
        await using var db = await dbFactory.CreateDbContextAsync();

        var tenantIds = await db.Tenants.Select(t => t.Id).ToListAsync();
        if (tenantIds.Count == 0) return;

        var seededTenants = await db.AgentDefinitions
            .IgnoreQueryFilters()
            .Select(a => a.TenantId)
            .Distinct()
            .ToListAsync();

        var unseeded = tenantIds.Except(seededTenants).ToList();
        if (unseeded.Count == 0) return;

        foreach (var tenantId in unseeded)
        {
            db.AgentDefinitions.AddRange(BuildDefaults(tenantId));
        }

        await db.SaveChangesAsync();
    }

    private static IEnumerable<AgentDefinitionEntity> BuildDefaults(Guid tenantId)
    {
        return
        [
            new AgentDefinitionEntity
            {
                Id             = Guid.NewGuid().ToString("N"),
                TenantId       = tenantId,
                Name           = "Orchestrator",
                Icon           = "🧠",
                Description    = "Routes user requests to the right specialist agents and synthesises their results.",
                SystemPrompt   =
                    "You are the Pulse orchestrator. Delegate complete, self-contained sub-tasks to specialist agents " +
                    "via DelegateTo<Name>(task). You may call multiple specialists sequentially and combine their results. " +
                    "Answer directly only for trivial queries that need no tools.",
                IsEnabled      = true,
                IsOrchestrator = true,
                SortOrder      = 0,
                AllowedPluginKeysJson     = Serialize([]),
                AllowedSkillIdsJson       = Serialize([]),
                AllowedMcpServerIdsJson   = Serialize([]),
                AllowedDatabaseKeysJson   = Serialize([]),
                AllowedFlatFileIdsJson    = Serialize([]),
                AllowedRagDocumentIdsJson = Serialize([]),
            },
            new AgentDefinitionEntity
            {
                Id             = Guid.NewGuid().ToString("N"),
                TenantId       = tenantId,
                Name           = "DataAnalyst",
                Icon           = "📊",
                Description    = "Queries databases, analyses flat files, and renders charts.",
                SystemPrompt   = "You are a data analyst specialist. Use your database, flat-file, and chart tools to answer data questions precisely and concisely.",
                IsEnabled      = true,
                IsOrchestrator = false,
                SortOrder      = 1,
                AllowedPluginKeysJson     = Serialize([AgentDefinition.PluginKeys.Database, AgentDefinition.PluginKeys.Excel, AgentDefinition.PluginKeys.Chart, AgentDefinition.PluginKeys.FlatFileData]),
                AllowedSkillIdsJson       = Serialize([]),
                AllowedMcpServerIdsJson   = Serialize([]),
                AllowedDatabaseKeysJson   = Serialize([]),
                AllowedFlatFileIdsJson    = Serialize([]),
                AllowedRagDocumentIdsJson = Serialize([]),
            },
            new AgentDefinitionEntity
            {
                Id             = Guid.NewGuid().ToString("N"),
                TenantId       = tenantId,
                Name           = "DocumentWriter",
                Icon           = "📝",
                Description    = "Creates and edits PDF and Word documents.",
                SystemPrompt   = "You are a document-writing specialist. Produce well-structured PDF and Word documents using your document tools.",
                IsEnabled      = true,
                IsOrchestrator = false,
                SortOrder      = 2,
                AllowedPluginKeysJson     = Serialize([AgentDefinition.PluginKeys.Pdf, AgentDefinition.PluginKeys.Word]),
                AllowedSkillIdsJson       = Serialize([]),
                AllowedMcpServerIdsJson   = Serialize([]),
                AllowedDatabaseKeysJson   = Serialize([]),
                AllowedFlatFileIdsJson    = Serialize([]),
                AllowedRagDocumentIdsJson = Serialize([]),
            },
            new AgentDefinitionEntity
            {
                Id             = Guid.NewGuid().ToString("N"),
                TenantId       = tenantId,
                Name           = "Researcher",
                Icon           = "🔍",
                Description    = "Searches RAG knowledge bases and external MCP sources.",
                SystemPrompt   = "You are a research specialist. Retrieve relevant information from the knowledge base and external tools to answer questions accurately.",
                IsEnabled      = true,
                IsOrchestrator = false,
                SortOrder      = 3,
                AllowedPluginKeysJson     = Serialize([AgentDefinition.PluginKeys.Rag, AgentDefinition.PluginKeys.Mcp]),
                AllowedSkillIdsJson       = Serialize([]),
                AllowedMcpServerIdsJson   = Serialize([]),
                AllowedDatabaseKeysJson   = Serialize([]),
                AllowedFlatFileIdsJson    = Serialize([]),
                AllowedRagDocumentIdsJson = Serialize([]),
            },
            new AgentDefinitionEntity
            {
                Id             = Guid.NewGuid().ToString("N"),
                TenantId       = tenantId,
                Name           = "Ops",
                Icon           = "⚙️",
                Description    = "Runs terminal commands and sends emails via AgentMail.",
                SystemPrompt   = "You are an operations specialist. Execute terminal commands and send emails as directed. Always confirm destructive operations before proceeding.",
                IsEnabled      = true,
                IsOrchestrator = false,
                SortOrder      = 4,
                AllowedPluginKeysJson     = Serialize([AgentDefinition.PluginKeys.Terminal, AgentDefinition.PluginKeys.AgentMail]),
                AllowedSkillIdsJson       = Serialize([]),
                AllowedMcpServerIdsJson   = Serialize([]),
                AllowedDatabaseKeysJson   = Serialize([]),
                AllowedFlatFileIdsJson    = Serialize([]),
                AllowedRagDocumentIdsJson = Serialize([]),
            },
        ];
    }

    private static string Serialize(List<string> list) => JsonSerializer.Serialize(list);
}
