using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Pulse.Data;
using Pulse.Data.Entities;
using Pulse.Models;
using System.Text.Json;

namespace Pulse.Services;

/// <summary>
/// Singleton service that persists all app settings to SQL Server, one row per tenant.
/// Uses <see cref="IMemoryCache"/> keyed by <see cref="Guid"/> tenantId with a 5-minute
/// sliding expiration. All mutating methods accept a <paramref name="tenantId"/> and
/// invalidate the relevant cache entry after persisting.
/// </summary>
public sealed class LlmSettingsService
{
    private readonly IDbContextFactory<ApplicationDbContext> _dbFactory;
    private readonly IMemoryCache _cache;
    private readonly GlobalLlmSettingsService _globalLlm;

    private static string CacheKey(Guid tenantId) => $"LlmSettings_{tenantId:N}";

    private static readonly MemoryCacheEntryOptions CacheOptions = new MemoryCacheEntryOptions()
        .SetSlidingExpiration(TimeSpan.FromMinutes(5));

    public LlmSettingsService(
        IDbContextFactory<ApplicationDbContext> dbFactory,
        IMemoryCache cache,
        GlobalLlmSettingsService globalLlm)
    {
        _dbFactory = dbFactory;
        _cache     = cache;
        _globalLlm = globalLlm;
    }

    // ── Read ────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Returns the cached <see cref="LlmSettingsModel"/> for the given tenant.
    /// Loads from DB on first call; subsequent calls return the cached copy.
    /// <see cref="LlmSettingsModel.ModelId"/> is always overwritten with the global value
    /// managed by SuperAdmin, so every tenant uses the same model regardless of any
    /// stale per-tenant value on disk.
    /// </summary>
    public async Task<LlmSettingsModel> GetAsync(Guid tenantId)
    {
        var key = CacheKey(tenantId);
        LlmSettingsModel model;
        if (_cache.TryGetValue(key, out LlmSettingsModel? cached) && cached != null)
        {
            model = cached;
        }
        else
        {
            model = await LoadFromDbAsync(tenantId);
            _cache.Set(key, model, CacheOptions);
        }

        // Global model shadows any per-tenant row value.
        var global = await _globalLlm.GetAsync();
        model.ModelId = global.ModelId ?? string.Empty;
        return model;
    }

    /// <summary>
    /// Synchronous no-arg overload kept for callers not yet migrated to the per-tenant API.
    /// Throws <see cref="InvalidOperationException"/> — callers must be updated to pass tenantId.
    /// </summary>
    [Obsolete("Use GetAsync(Guid tenantId) instead. This overload will be removed once all callers are migrated.")]
    public LlmSettingsModel Get()
        => throw new InvalidOperationException(
            "LlmSettingsService.Get() requires a TenantId. Use GetAsync(tenantId) instead.");

    /// <summary>Returns all global API keys (not tenant-scoped).</summary>
    public async Task<List<string>> GetApiKeysAsync()
    {
        await using var db = await _dbFactory.CreateDbContextAsync();
        return await db.AppApiKeys.AsNoTracking()
            .OrderBy(k => k.SortOrder)
            .Select(k => k.Key)
            .ToListAsync();
    }

    /// <summary>Rebuilds the cache entry for a tenant. No-op if tenantId is empty.</summary>
    public async Task ReloadAsync(Guid tenantId = default)
    {
        if (tenantId == Guid.Empty) return;
        var loaded = await LoadFromDbAsync(tenantId);
        _cache.Set(CacheKey(tenantId), loaded, CacheOptions);
    }

    /// <summary>Removes a tenant's settings from the in-memory cache.</summary>
    public void InvalidateCache(Guid tenantId)
        => _cache.Remove(CacheKey(tenantId));

    // ── Full replace (admin bulk save) ─────────────────────────────────────────

    public async Task SaveAsync(Guid tenantId, LlmSettingsModel model)
    {
        await using var db = await _dbFactory.CreateDbContextAsync();
        await using var tx = await db.Database.BeginTransactionAsync();

        var row = await db.AppSettings
            .IgnoreQueryFilters()
            .FirstOrDefaultAsync(s => s.TenantId == tenantId);

        if (row is null)
        {
            row = new AppSettingsEntity { TenantId = tenantId };
            db.AppSettings.Add(row);
        }
        ApplyScalars(row, model);

        // Replace collections for this tenant wholesale.
        // TODO(multi-tenancy): AppApiKeyEntity has no TenantId yet; removes all keys.
        var apiKeys = await db.AppApiKeys.ToListAsync();
        var mcpServers = await db.McpServers.IgnoreQueryFilters()
            .Where(m => m.TenantId == tenantId).ToListAsync();
        var skills = await db.Skills.IgnoreQueryFilters()
            .Where(s => s.TenantId == tenantId).ToListAsync();
        var ragDocs = await db.RagDocuments.IgnoreQueryFilters()
            .Where(r => r.TenantId == tenantId).ToListAsync();
        var flatFiles = await db.FlatFileSources.IgnoreQueryFilters()
            .Where(f => f.TenantId == tenantId).ToListAsync();
        var dbConns = await db.DatabaseConnections.IgnoreQueryFilters()
            .Where(d => d.TenantId == tenantId).ToListAsync();
        var agentDefs = await db.AgentDefinitions.IgnoreQueryFilters()
            .Where(a => a.TenantId == tenantId).ToListAsync();

        db.AppApiKeys.RemoveRange(apiKeys);
        db.McpServers.RemoveRange(mcpServers);
        db.Skills.RemoveRange(skills);
        db.RagDocuments.RemoveRange(ragDocs);
        db.FlatFileSources.RemoveRange(flatFiles);
        db.DatabaseConnections.RemoveRange(dbConns);
        db.AgentDefinitions.RemoveRange(agentDefs);
        await db.SaveChangesAsync();

        // TODO(multi-tenancy): AppApiKeyEntity does not yet have TenantId; keys are shared across tenants for now.
        db.AppApiKeys.AddRange((model.ApiKeys ?? []).Select((k, i) =>
            new AppApiKeyEntity { Key = k, SortOrder = i }));
        db.McpServers.AddRange((model.McpServers ?? []).Select(c => ToEntity(c, tenantId)));
        db.Skills.AddRange((model.Skills ?? []).Select(c => ToEntity(c, tenantId)));
        db.RagDocuments.AddRange((model.RagDocuments ?? []).Select(d => ToEntity(d, tenantId)));
        db.FlatFileSources.AddRange((model.FlatFileSources ?? []).Select(s => ToEntity(s, tenantId)));
        db.DatabaseConnections.AddRange(
            (model.DatabaseConnections ?? new(StringComparer.OrdinalIgnoreCase))
                .Select(kv => ToEntity(kv.Key, kv.Value, tenantId)));
        db.AgentDefinitions.AddRange((model.AgentDefinitions ?? []).Select(a => ToEntity(a, tenantId)));

        await db.SaveChangesAsync();
        await tx.CommitAsync();

        var refreshed = await LoadFromDbAsync(tenantId, db);
        _cache.Set(CacheKey(tenantId), refreshed, CacheOptions);
    }

    // ── MCP helpers ─────────────────────────────────────────────────────────────

    public async Task AddOrUpdateMcpServerAsync(Guid tenantId, McpServerConfig config) =>
        await UpsertCollectionAsync(tenantId, db => db.McpServers, config.Id,
            e => ApplyMcp(e, config), () => ToEntity(config, tenantId));

    public async Task DeleteMcpServerAsync(Guid tenantId, string id) =>
        await DeleteCollectionAsync(tenantId, db => db.McpServers, id);

    public async Task ToggleMcpServerAsync(Guid tenantId, string id) =>
        await ToggleCollectionAsync(tenantId, db => db.McpServers, id, e => e.IsEnabled = !e.IsEnabled);

    // ── Skill helpers ────────────────────────────────────────────────────────────

    public async Task AddOrUpdateSkillAsync(Guid tenantId, SkillConfig config) =>
        await UpsertCollectionAsync(tenantId, db => db.Skills, config.Id,
            e => ApplySkill(e, config), () => ToEntity(config, tenantId));

    public async Task DeleteSkillAsync(Guid tenantId, string id) =>
        await DeleteCollectionAsync(tenantId, db => db.Skills, id);

    public async Task ToggleSkillAsync(Guid tenantId, string id) =>
        await ToggleCollectionAsync(tenantId, db => db.Skills, id, e => e.IsActive = !e.IsActive);

    // ── RAG helpers ──────────────────────────────────────────────────────────────

    public async Task AddOrUpdateRagDocumentAsync(Guid tenantId, RagDocument doc) =>
        await UpsertCollectionAsync(tenantId, db => db.RagDocuments, doc.Id,
            e => ApplyRag(e, doc), () => ToEntity(doc, tenantId));

    public async Task DeleteRagDocumentAsync(Guid tenantId, string id) =>
        await DeleteCollectionAsync(tenantId, db => db.RagDocuments, id);

    public async Task ToggleRagDocumentAsync(Guid tenantId, string id) =>
        await ToggleCollectionAsync(tenantId, db => db.RagDocuments, id, e => e.IsEnabled = !e.IsEnabled);

    // ── Database Connection helpers ──────────────────────────────────────────────

    public async Task AddOrUpdateDatabaseConnectionAsync(Guid tenantId, string id, DatabaseConnectionEntry entry) =>
        await UpsertCollectionAsync(tenantId, db => db.DatabaseConnections, id,
            e => ApplyDbConn(e, entry), () => ToEntity(id, entry, tenantId));

    public async Task DeleteDatabaseConnectionAsync(Guid tenantId, string id) =>
        await DeleteCollectionAsync(tenantId, db => db.DatabaseConnections, id);

    public async Task ToggleDatabaseConnectionAsync(Guid tenantId, string id) =>
        await ToggleCollectionAsync(tenantId, db => db.DatabaseConnections, id, e => e.IsEnabled = !e.IsEnabled);

    // ── AgentDefinition helpers ──────────────────────────────────────────────────

    public async Task AddOrUpdateAgentDefinitionAsync(Guid tenantId, AgentDefinition agent)
    {
        if (agent.IsOrchestrator)
        {
            await using var check = await _dbFactory.CreateDbContextAsync();
            var existing = await check.AgentDefinitions.IgnoreQueryFilters()
                .FirstOrDefaultAsync(a => a.TenantId == tenantId && a.IsOrchestrator && a.Id != agent.Id);
            if (existing is not null)
                throw new InvalidOperationException(
                    $"Tenant already has an orchestrator agent (\"{existing.Name}\"). Clear IsOrchestrator on that row first.");
        }
        await UpsertCollectionAsync(tenantId, db => db.AgentDefinitions, agent.Id,
            e => ApplyAgentDefinition(e, agent), () => ToEntity(agent, tenantId));
    }

    public async Task DeleteAgentDefinitionAsync(Guid tenantId, string id) =>
        await DeleteCollectionAsync(tenantId, db => db.AgentDefinitions, id);

    public async Task ToggleAgentDefinitionAsync(Guid tenantId, string id) =>
        await ToggleCollectionAsync(tenantId, db => db.AgentDefinitions, id, e => e.IsEnabled = !e.IsEnabled);

    /// <summary>
    /// Returns the orchestrator <see cref="AgentDefinition"/> for <paramref name="tenantId"/>,
    /// or a hardcoded fallback default if none exists yet (e.g. before seeder completes).
    /// </summary>
    public async Task<AgentDefinition> GetOrchestratorAsync(Guid tenantId)
    {
        var settings = await GetAsync(tenantId);
        return settings.AgentDefinitions.FirstOrDefault(a => a.IsOrchestrator)
            ?? new AgentDefinition
            {
                Id             = "orchestrator-default",
                Name           = "Orchestrator",
                Icon           = "🧠",
                IsOrchestrator = true,
                IsEnabled      = true,
                SystemPrompt   =
                    "You are the Pulse orchestrator. Delegate complete, self-contained sub-tasks to specialist agents via DelegateTo<Name>(task). " +
                    "You may call multiple specialists sequentially and combine their results. " +
                    "Answer directly only for trivial queries that need no tools."
            };
    }

    // ── Flat-file data source helpers ────────────────────────────────────────────

    public async Task AddOrUpdateFlatFileSourceAsync(Guid tenantId, FlatFileDataSource source) =>
        await UpsertCollectionAsync(tenantId, db => db.FlatFileSources, source.Id,
            e => ApplyFlatFile(e, source), () => ToEntity(source, tenantId));

    public async Task DeleteFlatFileSourceAsync(Guid tenantId, string id) =>
        await DeleteCollectionAsync(tenantId, db => db.FlatFileSources, id);

    public async Task ToggleFlatFileSourceAsync(Guid tenantId, string id) =>
        await ToggleCollectionAsync(tenantId, db => db.FlatFileSources, id, e => e.IsEnabled = !e.IsEnabled);

    // ── Scalar/owned section helpers ─────────────────────────────────────────────

    public Task SaveTerminalSettingsAsync(Guid tenantId, TerminalSettings settings) =>
        UpdateScalarAsync(tenantId, row => row.Terminal = Clone(settings));

    public Task SaveWebSearchSettingsAsync(Guid tenantId, WebSearchSettings settings) =>
        UpdateScalarAsync(tenantId, row => row.WebSearch = Clone(settings));

    public Task SaveAgentMailSettingsAsync(AgentMailSettings settings) =>
        Task.CompletedTask; // TODO(multi-tenancy): AgentMail moved to GlobalAgentMailSettings

    public Task SaveSecuritySettingsAsync(Guid tenantId, SecuritySettings settings) =>
        UpdateScalarAsync(tenantId, row => row.Security = Clone(settings));

    public Task SaveAppNameAsync(Guid tenantId, string name) =>
        UpdateScalarAsync(tenantId, row => row.AppName = name.Trim());

    public Task SaveLogoAsync(Guid tenantId, string? fileName) =>
        UpdateScalarAsync(tenantId, row =>
        {
            row.LogoFileName = fileName;
            row.LogoVersion  = fileName is null ? null : Guid.NewGuid().ToString("N");
        });

    // ── Internals: generic upsert/delete/toggle on a collection DbSet ───────────

    private async Task UpsertCollectionAsync<TEntity>(
        Guid tenantId,
        Func<ApplicationDbContext, DbSet<TEntity>> set,
        string id,
        Action<TEntity> applyExisting,
        Func<TEntity> createNew) where TEntity : class
    {
        await using var db = await _dbFactory.CreateDbContextAsync();
        var table    = set(db);
        var existing = await table.FindAsync(id);
        if (existing is null)
            table.Add(createNew());
        else
            applyExisting(existing);
        await db.SaveChangesAsync();

        var refreshed = await LoadFromDbAsync(tenantId, db);
        _cache.Set(CacheKey(tenantId), refreshed, CacheOptions);
    }

    private async Task DeleteCollectionAsync<TEntity>(
        Guid tenantId,
        Func<ApplicationDbContext, DbSet<TEntity>> set,
        string id) where TEntity : class
    {
        await using var db = await _dbFactory.CreateDbContextAsync();
        var table    = set(db);
        var existing = await table.FindAsync(id);
        if (existing is not null)
        {
            table.Remove(existing);
            await db.SaveChangesAsync();
        }

        var refreshed = await LoadFromDbAsync(tenantId, db);
        _cache.Set(CacheKey(tenantId), refreshed, CacheOptions);
    }

    private async Task ToggleCollectionAsync<TEntity>(
        Guid tenantId,
        Func<ApplicationDbContext, DbSet<TEntity>> set,
        string id,
        Action<TEntity> toggle) where TEntity : class
    {
        await using var db = await _dbFactory.CreateDbContextAsync();
        var existing = await set(db).FindAsync(id);
        if (existing is not null)
        {
            toggle(existing);
            await db.SaveChangesAsync();
        }

        var refreshed = await LoadFromDbAsync(tenantId, db);
        _cache.Set(CacheKey(tenantId), refreshed, CacheOptions);
    }

    private async Task UpdateScalarAsync(Guid tenantId, Action<AppSettingsEntity> mutate)
    {
        await using var db = await _dbFactory.CreateDbContextAsync();
        var row = await db.AppSettings
            .IgnoreQueryFilters()
            .FirstOrDefaultAsync(s => s.TenantId == tenantId);

        if (row is null)
        {
            row = new AppSettingsEntity { TenantId = tenantId };
            db.AppSettings.Add(row);
        }
        mutate(row);
        row.UpdatedAtUtc = DateTime.UtcNow;
        await db.SaveChangesAsync();

        var refreshed = await LoadFromDbAsync(tenantId, db);
        _cache.Set(CacheKey(tenantId), refreshed, CacheOptions);
    }

    // ── Cache projection ─────────────────────────────────────────────────────────

    private async Task<LlmSettingsModel> LoadFromDbAsync(
        Guid tenantId, ApplicationDbContext? db = null)
    {
        var own = db is null;
        db ??= await _dbFactory.CreateDbContextAsync();
        try
        {
            var row  = await db.AppSettings.IgnoreQueryFilters().AsNoTracking()
                           .FirstOrDefaultAsync(s => s.TenantId == tenantId);
            // TODO(multi-tenancy): AppApiKeyEntity has no TenantId yet; all keys returned for any tenant.
            var keys = await db.AppApiKeys.AsNoTracking()
                           .OrderBy(k => k.SortOrder).Select(k => k.Key).ToListAsync();
            var mcp  = await db.McpServers.IgnoreQueryFilters().AsNoTracking()
                           .Where(m => m.TenantId == tenantId).ToListAsync();
            var sk   = await db.Skills.IgnoreQueryFilters().AsNoTracking()
                           .Where(s => s.TenantId == tenantId).ToListAsync();
            var rag  = await db.RagDocuments.IgnoreQueryFilters().AsNoTracking()
                           .Where(r => r.TenantId == tenantId).ToListAsync();
            var ff   = await db.FlatFileSources.IgnoreQueryFilters().AsNoTracking()
                           .Where(f => f.TenantId == tenantId).ToListAsync();
            var dbc  = await db.DatabaseConnections.IgnoreQueryFilters().AsNoTracking()
                           .Where(d => d.TenantId == tenantId).ToListAsync();
            var agents = await db.AgentDefinitions.IgnoreQueryFilters().AsNoTracking()
                           .Where(a => a.TenantId == tenantId)
                           .OrderBy(a => a.SortOrder).ToListAsync();

            return new LlmSettingsModel
            {
                AppName          = row?.AppName      ?? "Pulse",
                ModelId          = row?.ModelId      ?? "",
                LogoFileName     = row?.LogoFileName,
                LogoVersion      = row?.LogoVersion,
                TerminalSettings = row?.Terminal     ?? new(),
                WebSearch        = NormalizeWebSearch(row?.WebSearch),
                AgentMail        = new(), // TODO(multi-tenancy): AgentMail moved to GlobalAgentMailSettings
                Security         = row?.Security     ?? new(),
                ApiKeys          = keys,
                McpServers       = mcp.Select(FromEntity).ToList(),
                Skills           = sk.Select(FromEntity).ToList(),
                RagDocuments     = rag.Select(FromEntity).ToList(),
                FlatFileSources  = ff.Select(FromEntity).ToList(),
                DatabaseConnections = dbc.ToDictionary(
                    e => e.Id,
                    FromEntity,
                    StringComparer.OrdinalIgnoreCase),
                AgentDefinitions = agents.Select(FromEntity).ToList(),
            };
        }
        finally
        {
            if (own) await db.DisposeAsync();
        }
    }

    // ── Mapping ───────────────────────────────────────────────────────────────────

    private static void ApplyScalars(AppSettingsEntity row, LlmSettingsModel m)
    {
        row.AppName      = m.AppName;
        row.ModelId      = m.ModelId;
        row.LogoFileName = m.LogoFileName;
        row.LogoVersion  = m.LogoVersion;
        row.Terminal     = Clone(m.TerminalSettings ?? new());
        row.WebSearch    = Clone(m.WebSearch ?? new());
        // TODO(multi-tenancy): AgentMail moved to GlobalAgentMailSettings
        row.Security     = Clone(m.Security ?? new());
        row.UpdatedAtUtc = DateTime.UtcNow;
    }

    private static WebSearchSettings NormalizeWebSearch(WebSearchSettings? s) => new()
    {
        IsEnabled  = s?.IsEnabled  ?? false,
        MaxResults = (s?.MaxResults ?? 0) == 0 ? 5 : s!.MaxResults,
    };
    private static TerminalSettings Clone(TerminalSettings s) => new()
    {
        IsEnabled = s.IsEnabled, DefaultShell = s.DefaultShell, WorkingDirectory = s.WorkingDirectory,
        TimeoutSeconds = s.TimeoutSeconds, MaxOutputLength = s.MaxOutputLength
    };
    private static WebSearchSettings Clone(WebSearchSettings s) => new()
    {
        IsEnabled = s.IsEnabled, MaxResults = s.MaxResults
    };
    private static AgentMailSettings Clone(AgentMailSettings s) => new()
    {
        IsEnabled = s.IsEnabled, ApiKey = s.ApiKey, BaseUrl = s.BaseUrl, DefaultInbox = s.DefaultInbox
    };
    private static SecuritySettings Clone(SecuritySettings s) => new()
    {
        MaxFailedLoginAttempts = s.MaxFailedLoginAttempts, LoginLockoutHours = s.LoginLockoutHours
    };

    private static McpServerEntity ToEntity(McpServerConfig c, Guid tenantId) => new()
    {
        Id = c.Id, Name = c.Name, TransportType = c.TransportType,
        Url = c.Url, Command = c.Command, Arguments = c.Arguments,
        ApiKey = c.ApiKey, IsEnabled = c.IsEnabled, TenantId = tenantId
    };
    private static void ApplyMcp(McpServerEntity e, McpServerConfig c)
    {
        e.Name = c.Name; e.TransportType = c.TransportType;
        e.Url = c.Url; e.Command = c.Command; e.Arguments = c.Arguments;
        e.ApiKey = c.ApiKey; e.IsEnabled = c.IsEnabled;
    }
    private static McpServerConfig FromEntity(McpServerEntity e) => new()
    {
        Id = e.Id, Name = e.Name, TransportType = e.TransportType,
        Url = e.Url, Command = e.Command, Arguments = e.Arguments,
        ApiKey = e.ApiKey, IsEnabled = e.IsEnabled
    };

    private static SkillEntity ToEntity(SkillConfig c, Guid tenantId) => new()
    {
        Id = c.Id, Name = c.Name, Icon = c.Icon, Description = c.Description,
        Instructions = c.Instructions, IsActive = c.IsActive, TenantId = tenantId
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

    private static RagDocumentEntity ToEntity(RagDocument d, Guid tenantId) => new()
    {
        Id = d.Id, Name = d.Name, Description = d.Description, IsEnabled = d.IsEnabled,
        OriginalFileName = d.OriginalFileName, ChunkCount = d.ChunkCount, UpdatedAt = d.UpdatedAt,
        TenantId = tenantId
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

    private static FlatFileSourceEntity ToEntity(FlatFileDataSource s, Guid tenantId) => new()
    {
        Id = s.Id, Label = s.Label, Format = s.Format, FilePath = s.FilePath, IsEnabled = s.IsEnabled,
        HasHeaders = s.HasHeaders, Delimiter = s.Delimiter, SheetName = s.SheetName,
        Encoding = s.Encoding, MaxRows = s.MaxRows,
        FixedWidthColumnsJson = JsonSerializer.Serialize(s.FixedWidthColumns ?? []),
        TenantId = tenantId
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

    private static DatabaseConnectionEntity ToEntity(string id, DatabaseConnectionEntry d, Guid tenantId) => new()
    {
        Id = id, Label = d.Label, Provider = d.Provider, ConnectionString = d.ConnectionString,
        IsEnabled = d.IsEnabled, ReadOnly = d.ReadOnly, MaxRows = d.MaxRows,
        AllowedSchemasJson = JsonSerializer.Serialize(d.AllowedSchemas ?? []),
        TenantId = tenantId
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

    private static AgentDefinitionEntity ToEntity(AgentDefinition a, Guid tenantId) => new()
    {
        Id = a.Id, Name = a.Name, Icon = a.Icon, Description = a.Description,
        SystemPrompt = a.SystemPrompt, ModelId = a.ModelId,
        IsEnabled = a.IsEnabled, IsOrchestrator = a.IsOrchestrator, SortOrder = a.SortOrder,
        AllowedPluginKeysJson    = JsonSerializer.Serialize(a.AllowedPluginKeys    ?? []),
        AllowedSkillIdsJson      = JsonSerializer.Serialize(a.AllowedSkillIds      ?? []),
        AllowedMcpServerIdsJson  = JsonSerializer.Serialize(a.AllowedMcpServerIds  ?? []),
        AllowedDatabaseKeysJson  = JsonSerializer.Serialize(a.AllowedDatabaseKeys  ?? []),
        AllowedFlatFileIdsJson   = JsonSerializer.Serialize(a.AllowedFlatFileIds   ?? []),
        AllowedRagDocumentIdsJson = JsonSerializer.Serialize(a.AllowedRagDocumentIds ?? []),
        TenantId = tenantId
    };
    private static void ApplyAgentDefinition(AgentDefinitionEntity e, AgentDefinition a)
    {
        e.Name = a.Name; e.Icon = a.Icon; e.Description = a.Description;
        e.SystemPrompt = a.SystemPrompt; e.ModelId = a.ModelId;
        e.IsEnabled = a.IsEnabled; e.IsOrchestrator = a.IsOrchestrator; e.SortOrder = a.SortOrder;
        e.AllowedPluginKeysJson    = JsonSerializer.Serialize(a.AllowedPluginKeys    ?? []);
        e.AllowedSkillIdsJson      = JsonSerializer.Serialize(a.AllowedSkillIds      ?? []);
        e.AllowedMcpServerIdsJson  = JsonSerializer.Serialize(a.AllowedMcpServerIds  ?? []);
        e.AllowedDatabaseKeysJson  = JsonSerializer.Serialize(a.AllowedDatabaseKeys  ?? []);
        e.AllowedFlatFileIdsJson   = JsonSerializer.Serialize(a.AllowedFlatFileIds   ?? []);
        e.AllowedRagDocumentIdsJson = JsonSerializer.Serialize(a.AllowedRagDocumentIds ?? []);
    }
    private static AgentDefinition FromEntity(AgentDefinitionEntity e) => new()
    {
        Id = e.Id, Name = e.Name, Icon = e.Icon, Description = e.Description,
        SystemPrompt = e.SystemPrompt, ModelId = e.ModelId,
        IsEnabled = e.IsEnabled, IsOrchestrator = e.IsOrchestrator, SortOrder = e.SortOrder,
        AllowedPluginKeys    = DeserializeList(e.AllowedPluginKeysJson),
        AllowedSkillIds      = DeserializeList(e.AllowedSkillIdsJson),
        AllowedMcpServerIds  = DeserializeList(e.AllowedMcpServerIdsJson),
        AllowedDatabaseKeys  = DeserializeList(e.AllowedDatabaseKeysJson),
        AllowedFlatFileIds   = DeserializeList(e.AllowedFlatFileIdsJson),
        AllowedRagDocumentIds = DeserializeList(e.AllowedRagDocumentIdsJson),
    };

    private static List<string> DeserializeList(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return [];
        try { return JsonSerializer.Deserialize<List<string>>(json) ?? []; }
        catch { return []; }
    }
}
