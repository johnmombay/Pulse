using Microsoft.EntityFrameworkCore;
using Pulse.Data;
using Pulse.Data.Entities;
using Pulse.Models;
using System.Text.Json;

namespace Pulse.Services;

/// <summary>
/// One-time migrator: if the <c>AppSettings</c> singleton row is missing, seed all
/// settings tables from <c>{ContentRoot}/llm-settings.json</c> (or <c>appsettings.json</c>
/// as a fallback). On success, renames the JSON file to <c>.migrated-&lt;utc&gt;</c>.
/// </summary>
public sealed class LlmSettingsSeeder
{
    private readonly IDbContextFactory<ApplicationDbContext> _dbFactory;
    private readonly IWebHostEnvironment _env;
    private readonly IConfiguration _configuration;
    private readonly ILogger<LlmSettingsSeeder> _log;

    public LlmSettingsSeeder(
        IDbContextFactory<ApplicationDbContext> dbFactory,
        IWebHostEnvironment env,
        IConfiguration configuration,
        ILogger<LlmSettingsSeeder> log)
    {
        _dbFactory = dbFactory;
        _env = env;
        _configuration = configuration;
        _log = log;
    }

    public async Task SeedAsync()
    {
        await using var db = await _dbFactory.CreateDbContextAsync();

        if (await db.AppSettings.AnyAsync())
        {
            _log.LogDebug("LlmSettings already seeded; skipping.");
            return;
        }

        var filePath = Path.Combine(_env.ContentRootPath, "llm-settings.json");
        LlmSettingsModel? source = null;

        if (File.Exists(filePath))
        {
            try
            {
                var json = await File.ReadAllTextAsync(filePath);
                source = JsonSerializer.Deserialize<LlmSettingsModel>(json);
            }
            catch (Exception ex)
            {
                _log.LogError(ex, "Failed to parse llm-settings.json; falling back to appsettings.json.");
            }
        }

        source ??= LoadFromConfiguration(_configuration);

        await using var tx = await db.Database.BeginTransactionAsync();

        db.AppSettings.Add(new AppSettingsEntity
        {
            Id           = 1,
            AppName      = source.AppName,
            ModelId      = source.ModelId,
            LogoFileName = source.LogoFileName,
            LogoVersion  = source.LogoVersion,
            Terminal     = source.TerminalSettings ?? new(),
            AgentMail    = source.AgentMail        ?? new(),
            Security     = source.Security         ?? new(),
            UpdatedAtUtc = DateTime.UtcNow,
        });

        db.AppApiKeys.AddRange((source.ApiKeys ?? [])
            .Select((k, i) => new AppApiKeyEntity { Key = k, SortOrder = i }));

        db.McpServers.AddRange((source.McpServers ?? []).Select(c => new McpServerEntity
        {
            Id = c.Id, Name = c.Name, TransportType = c.TransportType,
            Url = c.Url, Command = c.Command, Arguments = c.Arguments, IsEnabled = c.IsEnabled
        }));

        db.Skills.AddRange((source.Skills ?? []).Select(s => new SkillEntity
        {
            Id = s.Id, Name = s.Name, Icon = s.Icon, Description = s.Description,
            Instructions = s.Instructions, IsActive = s.IsActive
        }));

        db.RagDocuments.AddRange((source.RagDocuments ?? []).Select(r => new RagDocumentEntity
        {
            Id = r.Id, Name = r.Name, Description = r.Description, IsEnabled = r.IsEnabled,
            OriginalFileName = r.OriginalFileName, ChunkCount = r.ChunkCount, UpdatedAt = r.UpdatedAt
        }));

        db.FlatFileSources.AddRange((source.FlatFileSources ?? []).Select(f => new FlatFileSourceEntity
        {
            Id = f.Id, Label = f.Label, Format = f.Format, FilePath = f.FilePath, IsEnabled = f.IsEnabled,
            HasHeaders = f.HasHeaders, Delimiter = f.Delimiter, SheetName = f.SheetName,
            Encoding = f.Encoding, MaxRows = f.MaxRows,
            FixedWidthColumnsJson = JsonSerializer.Serialize(f.FixedWidthColumns ?? [])
        }));

        var conns = source.DatabaseConnections ?? new(StringComparer.OrdinalIgnoreCase);
        db.DatabaseConnections.AddRange(conns.Select(kv => new DatabaseConnectionEntity
        {
            Id = kv.Key, Label = kv.Value.Label, Provider = kv.Value.Provider,
            ConnectionString = kv.Value.ConnectionString,
            IsEnabled = kv.Value.IsEnabled, ReadOnly = kv.Value.ReadOnly, MaxRows = kv.Value.MaxRows,
            AllowedSchemasJson = JsonSerializer.Serialize(kv.Value.AllowedSchemas ?? [])
        }));

        await db.SaveChangesAsync();
        await tx.CommitAsync();

        _log.LogInformation(
            "Seeded LlmSettings from {Source} (skills={S}, mcp={M}, rag={R}, flat={F}, dbConns={D}, keys={K}).",
            File.Exists(filePath) ? filePath : "appsettings.json",
            source.Skills?.Count ?? 0, source.McpServers?.Count ?? 0,
            source.RagDocuments?.Count ?? 0, source.FlatFileSources?.Count ?? 0,
            conns.Count, source.ApiKeys?.Count ?? 0);

        if (File.Exists(filePath))
        {
            try
            {
                var stamp = DateTime.UtcNow.ToString("yyyyMMddHHmmss");
                File.Move(filePath, filePath + $".migrated-{stamp}", overwrite: false);
            }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "Seeded DB but could not rename {Path}.", filePath);
            }
        }
    }

    private static LlmSettingsModel LoadFromConfiguration(IConfiguration configuration)
    {
        var keys = configuration.GetSection("Gemini:ApiKeys").Get<string[]>() ?? [];
        return new LlmSettingsModel
        {
            ModelId = configuration["Gemini:ModelId"] ?? "gemini-2.0-flash",
            ApiKeys = [.. keys],
        };
    }
}
