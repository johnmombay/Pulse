using Microsoft.EntityFrameworkCore;
using Pulse.Data;
using Pulse.Data.Entities;
using Pulse.Models;
using System.Text.Json;

namespace Pulse.Services;

/// <summary>
/// Singleton service that persists all app settings to SQL Server (migrated from llm-settings.json).
/// Callers get a cached <see cref="LlmSettingsModel"/> via <see cref="Get"/>; every mutation rewrites
/// the relevant rows in one transaction and refreshes the cache.
/// </summary>
public sealed class LlmSettingsService
{
    private readonly IDbContextFactory<ApplicationDbContext> _dbFactory;
    private volatile LlmSettingsModel _cached = new();
    private readonly SemaphoreSlim _lock = new(1, 1);

    public LlmSettingsService(IDbContextFactory<ApplicationDbContext> dbFactory)
    {
        _dbFactory = dbFactory;
        // Initial cache load — synchronous to preserve previous startup contract.
        _cached = LoadFromDbAsync().GetAwaiter().GetResult();
    }

    /// <summary>Returns the cached settings (never null).</summary>
    public LlmSettingsModel Get() => _cached;

    /// <summary>Rebuilds the in-memory cache from the database. Call after external seeding.</summary>
    public async Task ReloadAsync()
    {
        await _lock.WaitAsync();
        try { _cached = await LoadFromDbAsync(); }
        finally { _lock.Release(); }
    }

    // ── Full replace (used by admin bulk save) ───────────────────────────────

    public async Task SaveAsync(LlmSettingsModel model)
    {
        await _lock.WaitAsync();
        try
        {
            await using var db = await _dbFactory.CreateDbContextAsync();
            await using var tx = await db.Database.BeginTransactionAsync();

            var row = await db.AppSettings.FirstOrDefaultAsync();
            if (row is null)
            {
                row = new AppSettingsEntity { Id = 1 };
                db.AppSettings.Add(row);
            }
            ApplyScalars(row, model);

            // Replace collections wholesale.
            db.AppApiKeys.RemoveRange(db.AppApiKeys);
            db.McpServers.RemoveRange(db.McpServers);
            db.Skills.RemoveRange(db.Skills);
            db.RagDocuments.RemoveRange(db.RagDocuments);
            db.FlatFileSources.RemoveRange(db.FlatFileSources);
            db.DatabaseConnections.RemoveRange(db.DatabaseConnections);
            await db.SaveChangesAsync();

            db.AppApiKeys.AddRange((model.ApiKeys ?? []).Select((k, i) => new AppApiKeyEntity { Key = k, SortOrder = i }));
            db.McpServers.AddRange((model.McpServers ?? []).Select(ToEntity));
            db.Skills.AddRange((model.Skills ?? []).Select(ToEntity));
            db.RagDocuments.AddRange((model.RagDocuments ?? []).Select(ToEntity));
            db.FlatFileSources.AddRange((model.FlatFileSources ?? []).Select(ToEntity));
            db.DatabaseConnections.AddRange(
                (model.DatabaseConnections ?? new(StringComparer.OrdinalIgnoreCase))
                    .Select(kv => ToEntity(kv.Key, kv.Value)));

            await db.SaveChangesAsync();
            await tx.CommitAsync();

            _cached = await LoadFromDbAsync(db);
        }
        finally { _lock.Release(); }
    }

    // ── MCP helpers ───────────────────────────────────────────────────────────

    public async Task AddOrUpdateMcpServerAsync(McpServerConfig config) =>
        await UpsertCollectionAsync(db => db.McpServers, config.Id, e => ApplyMcp(e, config),
            () => ToEntity(config));

    public async Task DeleteMcpServerAsync(string id) =>
        await DeleteCollectionAsync(db => db.McpServers, id);

    public async Task ToggleMcpServerAsync(string id) =>
        await ToggleCollectionAsync(db => db.McpServers, id, e => e.IsEnabled = !e.IsEnabled);

    // ── Skill helpers ─────────────────────────────────────────────────────────

    public async Task AddOrUpdateSkillAsync(SkillConfig config) =>
        await UpsertCollectionAsync(db => db.Skills, config.Id, e => ApplySkill(e, config),
            () => ToEntity(config));

    public async Task DeleteSkillAsync(string id) =>
        await DeleteCollectionAsync(db => db.Skills, id);

    public async Task ToggleSkillAsync(string id) =>
        await ToggleCollectionAsync(db => db.Skills, id, e => e.IsActive = !e.IsActive);

    // ── RAG helpers ───────────────────────────────────────────────────────────

    public async Task AddOrUpdateRagDocumentAsync(RagDocument doc) =>
        await UpsertCollectionAsync(db => db.RagDocuments, doc.Id, e => ApplyRag(e, doc),
            () => ToEntity(doc));

    public async Task DeleteRagDocumentAsync(string id) =>
        await DeleteCollectionAsync(db => db.RagDocuments, id);

    public async Task ToggleRagDocumentAsync(string id) =>
        await ToggleCollectionAsync(db => db.RagDocuments, id, e => e.IsEnabled = !e.IsEnabled);

    // ── Database Connection helpers ───────────────────────────────────────────

    public async Task AddOrUpdateDatabaseConnectionAsync(string id, DatabaseConnectionEntry entry) =>
        await UpsertCollectionAsync(db => db.DatabaseConnections, id, e => ApplyDbConn(e, entry),
            () => ToEntity(id, entry));

    public async Task DeleteDatabaseConnectionAsync(string id) =>
        await DeleteCollectionAsync(db => db.DatabaseConnections, id);

    public async Task ToggleDatabaseConnectionAsync(string id) =>
        await ToggleCollectionAsync(db => db.DatabaseConnections, id, e => e.IsEnabled = !e.IsEnabled);

    // ── Flat-file data source helpers ─────────────────────────────────────────

    public async Task AddOrUpdateFlatFileSourceAsync(FlatFileDataSource source) =>
        await UpsertCollectionAsync(db => db.FlatFileSources, source.Id, e => ApplyFlatFile(e, source),
            () => ToEntity(source));

    public async Task DeleteFlatFileSourceAsync(string id) =>
        await DeleteCollectionAsync(db => db.FlatFileSources, id);

    public async Task ToggleFlatFileSourceAsync(string id) =>
        await ToggleCollectionAsync(db => db.FlatFileSources, id, e => e.IsEnabled = !e.IsEnabled);

    // ── Scalar/owned section helpers ──────────────────────────────────────────

    public Task SaveTerminalSettingsAsync(TerminalSettings settings) =>
        UpdateScalarAsync(row => row.Terminal = Clone(settings));

    public Task SaveAgentMailSettingsAsync(AgentMailSettings settings) =>
        UpdateScalarAsync(row => row.AgentMail = Clone(settings));

    public Task SaveSecuritySettingsAsync(SecuritySettings settings) =>
        UpdateScalarAsync(row => row.Security = Clone(settings));

    public Task SaveAppNameAsync(string name) =>
        UpdateScalarAsync(row => row.AppName = name.Trim());

    public Task SaveLogoAsync(string? fileName) =>
        UpdateScalarAsync(row =>
        {
            row.LogoFileName = fileName;
            row.LogoVersion  = fileName is null ? null : Guid.NewGuid().ToString("N");
        });

    // ── Internals: generic upsert/delete/toggle on a collection DbSet ─────────

    private async Task UpsertCollectionAsync<TEntity>(
        Func<ApplicationDbContext, DbSet<TEntity>> set,
        string id,
        Action<TEntity> applyExisting,
        Func<TEntity> createNew) where TEntity : class
    {
        await _lock.WaitAsync();
        try
        {
            await using var db = await _dbFactory.CreateDbContextAsync();
            var table = set(db);
            var existing = await table.FindAsync(id);
            if (existing is null)
                table.Add(createNew());
            else
                applyExisting(existing);
            await db.SaveChangesAsync();
            _cached = await LoadFromDbAsync(db);
        }
        finally { _lock.Release(); }
    }

    private async Task DeleteCollectionAsync<TEntity>(
        Func<ApplicationDbContext, DbSet<TEntity>> set, string id) where TEntity : class
    {
        await _lock.WaitAsync();
        try
        {
            await using var db = await _dbFactory.CreateDbContextAsync();
            var table = set(db);
            var existing = await table.FindAsync(id);
            if (existing is not null)
            {
                table.Remove(existing);
                await db.SaveChangesAsync();
            }
            _cached = await LoadFromDbAsync(db);
        }
        finally { _lock.Release(); }
    }

    private async Task ToggleCollectionAsync<TEntity>(
        Func<ApplicationDbContext, DbSet<TEntity>> set, string id,
        Action<TEntity> toggle) where TEntity : class
    {
        await _lock.WaitAsync();
        try
        {
            await using var db = await _dbFactory.CreateDbContextAsync();
            var existing = await set(db).FindAsync(id);
            if (existing is not null)
            {
                toggle(existing);
                await db.SaveChangesAsync();
            }
            _cached = await LoadFromDbAsync(db);
        }
        finally { _lock.Release(); }
    }

    private async Task UpdateScalarAsync(Action<AppSettingsEntity> mutate)
    {
        await _lock.WaitAsync();
        try
        {
            await using var db = await _dbFactory.CreateDbContextAsync();
            var row = await db.AppSettings.FirstOrDefaultAsync();
            if (row is null)
            {
                row = new AppSettingsEntity { Id = 1 };
                db.AppSettings.Add(row);
            }
            mutate(row);
            row.UpdatedAtUtc = DateTime.UtcNow;
            await db.SaveChangesAsync();
            _cached = await LoadFromDbAsync(db);
        }
        finally { _lock.Release(); }
    }

    // ── Cache projection ─────────────────────────────────────────────────────

    private async Task<LlmSettingsModel> LoadFromDbAsync(ApplicationDbContext? db = null)
    {
        var own = db is null;
        db ??= await _dbFactory.CreateDbContextAsync();
        try
        {
            var row = await db.AppSettings.AsNoTracking().FirstOrDefaultAsync();
            var keys = await db.AppApiKeys.AsNoTracking().OrderBy(k => k.SortOrder).Select(k => k.Key).ToListAsync();
            var mcp  = await db.McpServers.AsNoTracking().ToListAsync();
            var sk   = await db.Skills.AsNoTracking().ToListAsync();
            var rag  = await db.RagDocuments.AsNoTracking().ToListAsync();
            var ff   = await db.FlatFileSources.AsNoTracking().ToListAsync();
            var dbc  = await db.DatabaseConnections.AsNoTracking().ToListAsync();

            return new LlmSettingsModel
            {
                AppName      = row?.AppName  ?? "Pulse",
                ModelId      = row?.ModelId  ?? "gemini-2.0-flash",
                LogoFileName = row?.LogoFileName,
                LogoVersion  = row?.LogoVersion,
                TerminalSettings = row?.Terminal  ?? new(),
                AgentMail        = row?.AgentMail ?? new(),
                Security         = row?.Security  ?? new(),
                ApiKeys      = keys,
                McpServers   = mcp.Select(FromEntity).ToList(),
                Skills       = sk.Select(FromEntity).ToList(),
                RagDocuments = rag.Select(FromEntity).ToList(),
                FlatFileSources = ff.Select(FromEntity).ToList(),
                DatabaseConnections = dbc.ToDictionary(
                    e => e.Id,
                    FromEntity,
                    StringComparer.OrdinalIgnoreCase),
            };
        }
        finally
        {
            if (own) await db.DisposeAsync();
        }
    }

    // ── Mapping ───────────────────────────────────────────────────────────────

    // LlmSettingsModel exposes POCO sections as direct fields; we mirror them onto the entity.
    // (LlmSettingsModel.TerminalSettings / AgentMail / Security become AppSettingsEntity.Terminal / AgentMail / Security.)
    private static void ApplyScalars(AppSettingsEntity row, LlmSettingsModel m)
    {
        row.AppName      = m.AppName;
        row.ModelId      = m.ModelId;
        row.LogoFileName = m.LogoFileName;
        row.LogoVersion  = m.LogoVersion;
        row.Terminal     = Clone(m.TerminalSettings ?? new());
        row.AgentMail    = Clone(m.AgentMail        ?? new());
        row.Security     = Clone(m.Security         ?? new());
        row.UpdatedAtUtc = DateTime.UtcNow;
    }

    private static TerminalSettings Clone(TerminalSettings s) => new()
    {
        IsEnabled = s.IsEnabled, DefaultShell = s.DefaultShell, WorkingDirectory = s.WorkingDirectory,
        TimeoutSeconds = s.TimeoutSeconds, MaxOutputLength = s.MaxOutputLength
    };
    private static AgentMailSettings Clone(AgentMailSettings s) => new()
    {
        IsEnabled = s.IsEnabled, ApiKey = s.ApiKey, BaseUrl = s.BaseUrl, DefaultInbox = s.DefaultInbox
    };
    private static SecuritySettings Clone(SecuritySettings s) => new()
    {
        MaxFailedLoginAttempts = s.MaxFailedLoginAttempts, LoginLockoutHours = s.LoginLockoutHours
    };

    private static McpServerEntity ToEntity(McpServerConfig c) => new()
    {
        Id = c.Id, Name = c.Name, TransportType = c.TransportType,
        Url = c.Url, Command = c.Command, Arguments = c.Arguments, IsEnabled = c.IsEnabled
    };
    private static void ApplyMcp(McpServerEntity e, McpServerConfig c)
    {
        e.Name = c.Name; e.TransportType = c.TransportType;
        e.Url = c.Url; e.Command = c.Command; e.Arguments = c.Arguments; e.IsEnabled = c.IsEnabled;
    }
    private static McpServerConfig FromEntity(McpServerEntity e) => new()
    {
        Id = e.Id, Name = e.Name, TransportType = e.TransportType,
        Url = e.Url, Command = e.Command, Arguments = e.Arguments, IsEnabled = e.IsEnabled
    };

    private static SkillEntity ToEntity(SkillConfig c) => new()
    {
        Id = c.Id, Name = c.Name, Icon = c.Icon, Description = c.Description,
        Instructions = c.Instructions, IsActive = c.IsActive
    };
    private static void ApplySkill(SkillEntity e, SkillConfig c)
    {
        e.Name = c.Name; e.Icon = c.Icon; e.Description = c.Description;
        e.Instructions = c.Instructions; e.IsActive = c.IsActive;
    }
    private static SkillConfig FromEntity(SkillEntity e) => new()
    {
        Id = e.Id, Name = e.Name, Icon = e.Icon, Description = e.Description,
        Instructions = e.Instructions, IsActive = e.IsActive
    };

    private static RagDocumentEntity ToEntity(RagDocument d) => new()
    {
        Id = d.Id, Name = d.Name, Description = d.Description, IsEnabled = d.IsEnabled,
        OriginalFileName = d.OriginalFileName, ChunkCount = d.ChunkCount, UpdatedAt = d.UpdatedAt
    };
    private static void ApplyRag(RagDocumentEntity e, RagDocument d)
    {
        e.Name = d.Name; e.Description = d.Description; e.IsEnabled = d.IsEnabled;
        e.OriginalFileName = d.OriginalFileName; e.ChunkCount = d.ChunkCount; e.UpdatedAt = d.UpdatedAt;
    }
    private static RagDocument FromEntity(RagDocumentEntity e) => new()
    {
        Id = e.Id, Name = e.Name, Description = e.Description, IsEnabled = e.IsEnabled,
        OriginalFileName = e.OriginalFileName, ChunkCount = e.ChunkCount, UpdatedAt = e.UpdatedAt
    };

    private static FlatFileSourceEntity ToEntity(FlatFileDataSource s) => new()
    {
        Id = s.Id, Label = s.Label, Format = s.Format, FilePath = s.FilePath, IsEnabled = s.IsEnabled,
        HasHeaders = s.HasHeaders, Delimiter = s.Delimiter, SheetName = s.SheetName,
        Encoding = s.Encoding, MaxRows = s.MaxRows,
        FixedWidthColumnsJson = JsonSerializer.Serialize(s.FixedWidthColumns ?? [])
    };
    private static void ApplyFlatFile(FlatFileSourceEntity e, FlatFileDataSource s)
    {
        e.Label = s.Label; e.Format = s.Format; e.FilePath = s.FilePath; e.IsEnabled = s.IsEnabled;
        e.HasHeaders = s.HasHeaders; e.Delimiter = s.Delimiter; e.SheetName = s.SheetName;
        e.Encoding = s.Encoding; e.MaxRows = s.MaxRows;
        e.FixedWidthColumnsJson = JsonSerializer.Serialize(s.FixedWidthColumns ?? []);
    }
    private static FlatFileDataSource FromEntity(FlatFileSourceEntity e) => new()
    {
        Id = e.Id, Label = e.Label, Format = e.Format, FilePath = e.FilePath, IsEnabled = e.IsEnabled,
        HasHeaders = e.HasHeaders, Delimiter = e.Delimiter, SheetName = e.SheetName,
        Encoding = e.Encoding, MaxRows = e.MaxRows,
        FixedWidthColumns = DeserializeList(e.FixedWidthColumnsJson)
    };

    private static DatabaseConnectionEntity ToEntity(string id, DatabaseConnectionEntry d) => new()
    {
        Id = id, Label = d.Label, Provider = d.Provider, ConnectionString = d.ConnectionString,
        IsEnabled = d.IsEnabled, ReadOnly = d.ReadOnly, MaxRows = d.MaxRows,
        AllowedSchemasJson = JsonSerializer.Serialize(d.AllowedSchemas ?? [])
    };
    private static void ApplyDbConn(DatabaseConnectionEntity e, DatabaseConnectionEntry d)
    {
        e.Label = d.Label; e.Provider = d.Provider; e.ConnectionString = d.ConnectionString;
        e.IsEnabled = d.IsEnabled; e.ReadOnly = d.ReadOnly; e.MaxRows = d.MaxRows;
        e.AllowedSchemasJson = JsonSerializer.Serialize(d.AllowedSchemas ?? []);
    }
    private static DatabaseConnectionEntry FromEntity(DatabaseConnectionEntity e) => new()
    {
        Label = e.Label, Provider = e.Provider, ConnectionString = e.ConnectionString,
        IsEnabled = e.IsEnabled, ReadOnly = e.ReadOnly, MaxRows = e.MaxRows,
        AllowedSchemas = DeserializeList(e.AllowedSchemasJson)
    };

    private static List<string> DeserializeList(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return [];
        try { return JsonSerializer.Deserialize<List<string>>(json) ?? []; }
        catch { return []; }
    }
}
