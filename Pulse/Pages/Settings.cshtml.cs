using Pulse.Data.Entities;
using Pulse.Infrastructure;
using Pulse.Models;
using Pulse.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using System.ComponentModel.DataAnnotations;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Net.Http.Headers;
using System.Text.Json;
using Microsoft.Extensions.Caching.Memory;

namespace Pulse.Pages;

[Authorize(Policy = "TenantAdminOrAbove")]
public class SettingsModel(
    LlmSettingsService settingsService,
    RagService ragService,
    MemoryService memoryService,
    IDatabaseTools databaseTools,
    IWebHostEnvironment env,
    ITenantContext tenantContext,
    GlobalAgentMailSettingsService globalAgentMail,
    GlobalLlmSettingsService globalLlm,
    ISubscriptionLimitService subscriptionLimits) : PageModel
{
    // TODO(multi-tenancy): Admin settings pages should validate the user belongs to this tenant.
    private Guid TenantId => tenantContext.TenantId ?? Guid.Empty;

    // ── LLM ──────────────────────────────────────────────────────────────────
    [BindProperty]
    public LlmInput Input { get; set; } = new();

    // ── MCP ──────────────────────────────────────────────────────────────────
    [BindProperty]
    public McpInput McpServer { get; set; } = new();

    public IReadOnlyList<McpServerConfig> McpServers { get; private set; } = [];

    // ── Skills ────────────────────────────────────────────────────────────────
    [BindProperty]
    public SkillInput Skill { get; set; } = new();

    public IReadOnlyList<SkillConfig> Skills { get; private set; } = [];

    // ── RAG ───────────────────────────────────────────────────────────────────
    [BindProperty]
    public RagDocInput RagDoc { get; set; } = new();

    public IReadOnlyList<RagDocument> RagDocuments { get; private set; } = [];

    // ── Memory ────────────────────────────────────────────────────────────────
    [BindProperty]
    public MemorySettingsInput MemInput { get; set; } = new();

    public IReadOnlyList<AgentMemory> Memories { get; private set; } = [];

    // ── Database Connections ──────────────────────────────────────────────────
    [BindProperty]
    public DbConnInput DbConn { get; set; } = new();

    public IReadOnlyList<(string Id, DatabaseConnectionEntry Entry)> DatabaseConnections
        { get; private set; } = [];

    // ── Flat-file data sources ─────────────────────────────────────────────────
    [BindProperty]
    public FlatFileInput FlatFile { get; set; } = new();

    [BindProperty]
    public IFormFile? FlatFileUpload { get; set; }

    // ── Logo ──────────────────────────────────────────────────────────────────
    [BindProperty]
    public IFormFile? LogoUpload { get; set; }

    public string? CurrentLogoFileName { get; private set; }
    public string? CurrentLogoVersion  { get; private set; }

    /// <summary>Per-tenant URL of the current logo (without the cache-busting <c>?v=</c> suffix).</summary>
    public string CurrentLogoUrl =>
        TenantId == Guid.Empty
            ? "/images/app-logo.png"
            : $"/images/tenants/{TenantId:N}/app-logo.png";

    public IReadOnlyList<FlatFileDataSource> FlatFileSources { get; private set; } = [];

    // ── Agents ────────────────────────────────────────────────────────────────
    [BindProperty]
    public AgentInput AgentDef { get; set; } = new();

    public IReadOnlyList<AgentDefinition> AgentDefinitions { get; private set; } = [];

    // ── Terminal ──────────────────────────────────────────────────────────────
    [BindProperty]
    public TerminalInput Terminal { get; set; } = new();

    // ── AgentMail ─────────────────────────────────────────────────────────────
    [BindProperty]
    public AgentMailInput AgentMail { get; set; } = new();

    // ── Security ──────────────────────────────────────────────────────────────
    [BindProperty]
    public SecurityInput Security { get; set; } = new();

    // ── App name ─────────────────────────────────────────────────────────────
    [BindProperty]
    public string AppName { get; set; } = "Pulse";

    private string UserId => User.FindFirstValue(ClaimTypes.NameIdentifier) ?? "";

    // ── Lifecycle ─────────────────────────────────────────────────────────────
    public async Task OnGetAsync()
    {
        var current = await settingsService.GetAsync(TenantId);
        Input.ModelId = current.ModelId;
        Input.ApiKeys = current.ApiKeys.Count > 0 ? [.. current.ApiKeys] : [""];
        AppName          = current.AppName ?? "Pulse";
        McpServers       = current.McpServers   ?? [];
        Skills           = current.Skills       ?? [];
        RagDocuments     = current.RagDocuments ?? [];
        Memories         = await memoryService.GetAllAsync(UserId);
        DatabaseConnections = (current.DatabaseConnections ?? [])
            .Select(kvp => (kvp.Key, kvp.Value))
            .OrderBy(t => t.Key)
            .ToList();

        FlatFileSources = (current.FlatFileSources ?? [])
            .OrderBy(s => s.Label)
            .ToList();

        AgentDefinitions = (current.AgentDefinitions ?? [])
            .OrderBy(a => a.SortOrder)
            .ThenBy(a => a.Name)
            .ToList();

        CurrentLogoFileName = current.LogoFileName;
        CurrentLogoVersion  = current.LogoVersion;

        var ts = current.TerminalSettings ?? new();
        Terminal = new TerminalInput
        {
            IsEnabled        = ts.IsEnabled,
            DefaultShell     = ts.DefaultShell,
            WorkingDirectory = ts.WorkingDirectory,
            TimeoutSeconds   = ts.TimeoutSeconds,
            MaxOutputLength  = ts.MaxOutputLength,
        };

        var am = await globalAgentMail.GetAsync();
        AgentMail = new AgentMailInput
        {
            IsEnabled    = am.IsEnabled,
            ApiKey       = am.ApiKey,
            BaseUrl      = string.IsNullOrWhiteSpace(am.ApiBaseUrl) ? "https://api.agentmail.to/v0" : am.ApiBaseUrl,
            DefaultInbox = am.DefaultInbox,
        };

        var sec = current.Security ?? new();
        Security = new SecurityInput
        {
            MaxFailedLoginAttempts = sec.MaxFailedLoginAttempts,
            LoginLockoutHours      = sec.LoginLockoutHours,
        };
    }

    // ── LLM save (default POST handler) ──────────────────────────────────────
    public async Task<IActionResult> OnPostAsync()
    {
        // LLM model + API keys are global infrastructure — only SuperAdmin may modify them.
        if (!User.IsInRole("SuperAdmin"))
            return Forbid();

        // The page hosts many [BindProperty] modal inputs (McpServer, Skill, RagDoc,
        // DbConn, FlatFile, AgentDef, MemInput, Terminal, AgentMail, Security, …) each
        // with [Required] fields that are NOT part of the LLM form. Keep only Input.*
        // ModelState entries so unrelated validation errors can't block this save.
        var nonLlmKeys = ModelState.Keys
            .Where(k => !k.StartsWith("Input.", StringComparison.Ordinal) && k != "Input")
            .ToList();
        foreach (var key in nonLlmKeys) ModelState.Remove(key);

        if (!ModelState.IsValid)
        {
            McpServers = (await settingsService.GetAsync(TenantId)).McpServers;
            return Page();
        }

        var cleanKeys = (Input.ApiKeys ?? [])
            .Select(k => k?.Trim() ?? string.Empty)
            .Where(k => k.Length > 0)
            .ToList();

        await globalLlm.SaveAsync(new Data.Entities.GlobalLlmSettings
        {
            ModelId = Input.ModelId.Trim(),
        });

        var current = await settingsService.GetAsync(TenantId);
        await settingsService.SaveAsync(TenantId, new LlmSettingsModel
        {
            AppName             = current.AppName ?? "Pulse",
            ModelId             = string.Empty,
            ApiKeys             = cleanKeys,
            McpServers          = current.McpServers          ?? [],
            Skills              = current.Skills              ?? [],
            RagDocuments        = current.RagDocuments        ?? [],
            DatabaseConnections = current.DatabaseConnections ?? new(StringComparer.OrdinalIgnoreCase),
            TerminalSettings    = current.TerminalSettings    ?? new(),
            AgentMail           = current.AgentMail           ?? new(),
            FlatFileSources     = current.FlatFileSources     ?? [],
            Security            = current.Security            ?? new(),
            LogoFileName        = current.LogoFileName,
            LogoVersion         = current.LogoVersion,
        });

        TempData["SaveSuccess"] = true;
        return RedirectToPage();
    }

    // ── OpenRouter: fetch model list (SuperAdmin only) ────────────────────────
    private static readonly IMemoryCache _openRouterModelCache =
        new MemoryCache(new MemoryCacheOptions());

    public async Task<IActionResult> OnGetOpenRouterModelsAsync([FromQuery] string apiKey)
    {
        if (!User.IsInRole("SuperAdmin")) return Forbid();
        if (string.IsNullOrWhiteSpace(apiKey))
            return new JsonResult(new { error = "API key is required." }) { StatusCode = 400 };

        var cacheKey = "OR_" + Convert.ToHexString(
            SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(apiKey)))[..16];

        if (_openRouterModelCache.TryGetValue(cacheKey, out object? cached))
            return new JsonResult(cached);

        using var http = new HttpClient();
        http.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", apiKey);
        http.Timeout = TimeSpan.FromSeconds(15);

        HttpResponseMessage resp;
        try
        {
            resp = await http.GetAsync("https://openrouter.ai/api/v1/models");
        }
        catch (Exception ex)
        {
            return new JsonResult(new { error = $"Could not reach OpenRouter: {ex.Message}" })
                { StatusCode = 502 };
        }

        if (resp.StatusCode == System.Net.HttpStatusCode.Unauthorized)
            return new JsonResult(new { error = "Invalid API key." }) { StatusCode = 401 };

        if (!resp.IsSuccessStatusCode)
            return new JsonResult(new { error = $"OpenRouter returned {(int)resp.StatusCode}." })
                { StatusCode = 502 };

        var json = await resp.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(json);

        var models = new List<object>();
        foreach (var item in doc.RootElement.GetProperty("data").EnumerateArray())
        {
            var id   = item.GetProperty("id").GetString() ?? "";
            var name = item.TryGetProperty("name", out var n) ? n.GetString() ?? id : id;
            var provider = id.Contains('/') ? id[..id.IndexOf('/')] : id;

            decimal inputCost  = 0m;
            decimal outputCost = 0m;
            if (item.TryGetProperty("pricing", out var pricing))
            {
                if (pricing.TryGetProperty("prompt", out var p) &&
                    decimal.TryParse(p.GetString(), System.Globalization.NumberStyles.Any,
                        System.Globalization.CultureInfo.InvariantCulture, out var pv))
                    inputCost = pv * 1_000_000m;

                if (pricing.TryGetProperty("completion", out var c) &&
                    decimal.TryParse(c.GetString(), System.Globalization.NumberStyles.Any,
                        System.Globalization.CultureInfo.InvariantCulture, out var cv))
                    outputCost = cv * 1_000_000m;
            }

            models.Add(new
            {
                id,
                name,
                provider,
                inputCost  = Math.Round(inputCost,  4),
                outputCost = Math.Round(outputCost, 4),
                isFree     = inputCost == 0m && outputCost == 0m
            });
        }

        if (models.Count == 0)
            return new JsonResult(new { error = "No models found for this key." }) { StatusCode = 200 };

        var result = new { models };
        _openRouterModelCache.Set(cacheKey, result,
            new MemoryCacheEntryOptions { AbsoluteExpirationRelativeToNow = TimeSpan.FromHours(1) });

        return new JsonResult(result);
    }

    // ── MCP: add or update (called by modal via fetch/AJAX) ──────────────────
    public async Task<IActionResult> OnPostSaveMcpAsync()
    {
        // Bypass ModelState entirely — validate manually so partial-form POSTs
        // (modal only sends McpServer.* fields) never get blocked by LLM field errors.
        if (string.IsNullOrWhiteSpace(McpServer?.Name))
            return new JsonResult(new { success = false, error = "Name is required." });

        if (McpServer.TransportType == "http" && string.IsNullOrWhiteSpace(McpServer.Url))
            return new JsonResult(new { success = false, error = "Server URL is required for HTTP transport." });

        if (McpServer.TransportType == "stdio" && string.IsNullOrWhiteSpace(McpServer.Command))
            return new JsonResult(new { success = false, error = "Command is required for stdio transport." });

        try
        {
            var isNew = string.IsNullOrWhiteSpace(McpServer.Id);
            var config = new McpServerConfig
            {
                Id            = isNew ? Guid.NewGuid().ToString("N") : McpServer.Id,
                Name          = McpServer.Name.Trim(),
                TransportType = McpServer.TransportType,
                Url           = McpServer.Url?.Trim(),
                Command       = McpServer.Command?.Trim(),
                Arguments     = McpServer.Arguments?.Trim(),
                IsEnabled     = McpServer.IsEnabled
            };

            await settingsService.AddOrUpdateMcpServerAsync(TenantId, config);

            return new JsonResult(new
            {
                success = true,
                message = $"MCP server \"{config.Name}\" {(isNew ? "added" : "updated")}."
            });
        }
        catch (Exception ex)
        {
            return new JsonResult(new { success = false, error = $"Save failed: {ex.Message}" })
            {
                StatusCode = 500
            };
        }
    }

    // ── MCP: delete ───────────────────────────────────────────────────────────
    public async Task<IActionResult> OnPostDeleteMcpAsync(string id)
    {
        await settingsService.DeleteMcpServerAsync(TenantId, id);
        TempData["McpSuccess"] = "MCP server removed.";
        return RedirectToPage();
    }

    // ── MCP: toggle enabled ───────────────────────────────────────────────────
    public async Task<IActionResult> OnPostToggleMcpAsync(string id)
    {
        await settingsService.ToggleMcpServerAsync(TenantId, id);
        return RedirectToPage();
    }

    // ── Skills: add or update (AJAX) ──────────────────────────────────────────
    public async Task<IActionResult> OnPostSaveSkillAsync()
    {
        if (string.IsNullOrWhiteSpace(Skill?.Name))
            return new JsonResult(new { success = false, error = "Name is required." });

        if (string.IsNullOrWhiteSpace(Skill.Instructions))
            return new JsonResult(new { success = false, error = "Instructions are required." });

        try
        {
            var isNew = string.IsNullOrWhiteSpace(Skill.Id);
            var config = new SkillConfig
            {
                Id           = isNew ? Guid.NewGuid().ToString("N") : Skill.Id,
                Name         = Skill.Name.Trim(),
                Icon         = string.IsNullOrWhiteSpace(Skill.Icon) ? "⚡" : Skill.Icon.Trim(),
                Description  = Skill.Description?.Trim() ?? "",
                Instructions = Skill.Instructions.Trim(),
                IsActive     = Skill.IsActive
            };

            await settingsService.AddOrUpdateSkillAsync(TenantId, config);

            return new JsonResult(new
            {
                success = true,
                message = $"Skill \"{config.Name}\" {(isNew ? "added" : "updated")}."
            });
        }
        catch (Exception ex)
        {
            return new JsonResult(new { success = false, error = $"Save failed: {ex.Message}" })
            {
                StatusCode = 500
            };
        }
    }

    // ── Skills: delete ────────────────────────────────────────────────────────
    public async Task<IActionResult> OnPostDeleteSkillAsync(string id)
    {
        await settingsService.DeleteSkillAsync(TenantId, id);
        TempData["SkillSuccess"] = "Skill removed.";
        return RedirectToPage();
    }

    // ── Skills: toggle active ─────────────────────────────────────────────────
    public async Task<IActionResult> OnPostToggleSkillAsync(string id)
    {
        await settingsService.ToggleSkillAsync(TenantId, id);
        return RedirectToPage();
    }

    // ── Agents: add or update (AJAX) ──────────────────────────────────────────
    public async Task<IActionResult> OnPostSaveAgentAsync()
    {
        if (string.IsNullOrWhiteSpace(AgentDef?.Name))
            return new JsonResult(new { success = false, error = "Name is required." });

        if (string.IsNullOrWhiteSpace(AgentDef.SystemPrompt))
            return new JsonResult(new { success = false, error = "System prompt is required." });

        var isNewAgent = string.IsNullOrWhiteSpace(AgentDef.Id);

        // Enforce subscription agent limit only when adding a new agent.
        if (isNewAgent && TenantId != Guid.Empty)
        {
            if (!await subscriptionLimits.CanAddAgentAsync(TenantId))
                return new JsonResult(new { success = false, error = "Your subscription plan's agent limit has been reached. Please upgrade your plan to add more agents." });
        }

        try
        {
            var agent = new AgentDefinition
            {
                Id                    = isNewAgent ? Guid.NewGuid().ToString("N") : AgentDef.Id,
                Name                  = AgentDef.Name.Trim(),
                Icon                  = string.IsNullOrWhiteSpace(AgentDef.Icon) ? "🤖" : AgentDef.Icon.Trim(),
                Description           = AgentDef.Description?.Trim() ?? "",
                SystemPrompt          = AgentDef.SystemPrompt.Trim(),
                ModelId               = string.IsNullOrWhiteSpace(AgentDef.ModelId) ? null : AgentDef.ModelId.Trim(),
                IsEnabled             = AgentDef.IsEnabled,
                IsOrchestrator        = AgentDef.IsOrchestrator,
                SortOrder             = AgentDef.SortOrder,
                AllowedPluginKeys     = AgentDef.AllowedPluginKeys     ?? [],
                AllowedSkillIds       = AgentDef.AllowedSkillIds       ?? [],
                AllowedMcpServerIds   = AgentDef.AllowedMcpServerIds   ?? [],
                AllowedDatabaseKeys   = AgentDef.AllowedDatabaseKeys   ?? [],
                AllowedFlatFileIds    = AgentDef.AllowedFlatFileIds    ?? [],
                AllowedRagDocumentIds = AgentDef.AllowedRagDocumentIds ?? [],
            };

            await settingsService.AddOrUpdateAgentDefinitionAsync(TenantId, agent);

            return new JsonResult(new
            {
                success = true,
                message = $"Agent \"{agent.Name}\" {(isNewAgent ? "added" : "updated")}."
            });
        }
        catch (InvalidOperationException ex)
        {
            return new JsonResult(new { success = false, error = ex.Message });
        }
        catch (Exception ex)
        {
            return new JsonResult(new { success = false, error = $"Save failed: {ex.Message}" })
            { StatusCode = 500 };
        }
    }

    // ── Agents: delete ────────────────────────────────────────────────────────
    public async Task<IActionResult> OnPostDeleteAgentAsync(string id)
    {
        var current = await settingsService.GetAsync(TenantId);
        var agent = current.AgentDefinitions.FirstOrDefault(a => a.Id == id);
        if (agent?.IsOrchestrator == true)
        {
            TempData["AgentError"] = "The orchestrator agent cannot be deleted. Disable it instead.";
            return RedirectToPage();
        }

        await settingsService.DeleteAgentDefinitionAsync(TenantId, id);
        TempData["AgentSuccess"] = "Agent removed.";
        return RedirectToPage();
    }

    // ── Agents: toggle enabled ────────────────────────────────────────────────
    public async Task<IActionResult> OnPostToggleAgentAsync(string id)
    {
        await settingsService.ToggleAgentDefinitionAsync(TenantId, id);
        return RedirectToPage();
    }

    // ── RAG: add or update (AJAX — supports file upload via FormData) ─────────
    public async Task<IActionResult> OnPostSaveRagAsync()
    {
        if (string.IsNullOrWhiteSpace(RagDoc?.Name))
            return new JsonResult(new { success = false, error = "Name is required." });

        bool isNew  = string.IsNullOrWhiteSpace(RagDoc.Id);
        var  docId  = isNew ? Guid.NewGuid().ToString("N") : RagDoc.Id;

        string? content  = null;
        string? fileName = null;

        if (RagDoc.SourceType == "file")
        {
            var upload = Request.Form.Files.GetFile("ragFileInput");
            if (upload is { Length: > 0 })
            {
                using var reader = new System.IO.StreamReader(
                    upload.OpenReadStream(), System.Text.Encoding.UTF8);
                content  = await reader.ReadToEndAsync();
                fileName = System.IO.Path.GetFileName(upload.FileName);
            }
            else if (isNew)
            {
                return new JsonResult(new { success = false, error = "Please select a file to upload." });
            }
            // editing without a new file → keep existing content
        }
        else
        {
            if (string.IsNullOrWhiteSpace(RagDoc.Content))
                return new JsonResult(new { success = false, error = "Content is required." });
            content = RagDoc.Content;
        }

        try
        {
            // Preserve existing metadata when editing without new content
            var existing       = (await settingsService.GetAsync(TenantId)).RagDocuments.FirstOrDefault(d => d.Id == docId);
            int chunkCount     = existing?.ChunkCount ?? 0;
            string? existFn    = existing?.OriginalFileName;
            string? embeddingWarning = null;

            if (content is not null)
            {
                var (chunks, embedded, embError) = await ragService.IngestAsync(content, docId, TenantId);
                chunkCount = chunks;

                if (embError is not null)
                    embeddingWarning =
                        $"Chunks were saved but embedding failed: {embError} " +
                        $"Keyword search is active as fallback. " +
                        $"Fix the API key / quota then use 🔄 Re-embed.";
            }

            var doc = new RagDocument
            {
                Id               = docId,
                Name             = RagDoc.Name.Trim(),
                Description      = RagDoc.Description?.Trim() ?? "",
                IsEnabled        = RagDoc.IsEnabled,
                OriginalFileName = fileName ?? existFn,
                ChunkCount       = chunkCount,
                UpdatedAt        = DateTime.UtcNow
            };

            await settingsService.AddOrUpdateRagDocumentAsync(TenantId, doc);

            var verb = isNew ? "added" : "updated";
            return new JsonResult(new
            {
                success = true,
                warning = embeddingWarning,
                message = $"\"{doc.Name}\" {verb} — {chunkCount} chunk(s) indexed."
            });
        }
        catch (Exception ex)
        {
            return new JsonResult(new { success = false, error = $"Failed: {ex.Message}" })
            {
                StatusCode = 500
            };
        }
    }

    // ── RAG: delete ───────────────────────────────────────────────────────────
    public async Task<IActionResult> OnPostDeleteRagAsync(string id)
    {
        ragService.DeleteDocument(id, TenantId);
        await settingsService.DeleteRagDocumentAsync(TenantId, id);
        TempData["RagSuccess"] = "RAG document removed.";
        return RedirectToPage();
    }

    // ── RAG: toggle enabled ───────────────────────────────────────────────────
    public async Task<IActionResult> OnPostToggleRagAsync(string id)
    {
        await settingsService.ToggleRagDocumentAsync(TenantId, id);
        return RedirectToPage();
    }

    // ── Database Connections: add or update (AJAX) ───────────────────────────
    public async Task<IActionResult> OnPostSaveDbConnAsync()
    {
        if (string.IsNullOrWhiteSpace(DbConn?.ConnectionId))
            return new JsonResult(new { success = false, error = "Connection ID is required." });

        if (string.IsNullOrWhiteSpace(DbConn.ConnectionString))
            return new JsonResult(new { success = false, error = "Connection string is required." });

        var id = DbConn.ConnectionId.Trim().ToLowerInvariant().Replace(' ', '-');

        // Enforce subscription database limit only when adding a new connection.
        var isNewDbConn = !(await settingsService.GetAsync(TenantId)).DatabaseConnections.ContainsKey(id);
        if (isNewDbConn && TenantId != Guid.Empty)
        {
            if (!await subscriptionLimits.CanAddDatabaseAsync(TenantId))
                return new JsonResult(new { success = false, error = "Your subscription plan's database/data source limit has been reached. Please upgrade your plan to add more connections." });
        }

        try
        {
            var entry = new DatabaseConnectionEntry
            {
                Label            = DbConn.Label?.Trim() ?? id,
                Provider         = DbConn.Provider,
                ConnectionString = DbConn.ConnectionString.Trim(),
                IsEnabled        = DbConn.IsEnabled,
                ReadOnly         = DbConn.ReadOnly,
                MaxRows          = DbConn.MaxRows > 0 ? DbConn.MaxRows : 500,
                AllowedSchemas   = (DbConn.AllowedSchemas ?? "")
                    .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                    .ToList()
            };

            await settingsService.AddOrUpdateDatabaseConnectionAsync(TenantId, id, entry);

            return new JsonResult(new
            {
                success = true,
                message = $"Connection \"{id}\" saved ({entry.Provider})."
            });
        }
        catch (Exception ex)
        {
            return new JsonResult(new { success = false, error = $"Save failed: {ex.Message}" })
            { StatusCode = 500 };
        }
    }

    // ── Database Connections: delete ─────────────────────────────────────────
    public async Task<IActionResult> OnPostDeleteDbConnAsync(string id)
    {
        await settingsService.DeleteDatabaseConnectionAsync(TenantId, id);
        TempData["DbConnSuccess"] = $"Connection \"{id}\" removed.";
        return RedirectToPage();
    }

    // ── Database Connections: toggle enabled ─────────────────────────────────
    public async Task<IActionResult> OnPostToggleDbConnAsync(string id)
    {
        await settingsService.ToggleDatabaseConnectionAsync(TenantId, id);
        return RedirectToPage();
    }

    // ── Database Connections: test (AJAX GET) ────────────────────────────────
    public async Task<IActionResult> OnGetTestDbConnAsync(string id)
    {
        try
        {
            var result = await databaseTools.ListTablesAsync(id, HttpContext.RequestAborted);
            var count  = result.GetArrayLength();
            return new JsonResult(new { success = true, message = $"Connected — {count} table(s) visible." });
        }
        catch (Exception ex)
        {
            return new JsonResult(new { success = false, error = ex.Message });
        }
    }

    // ── Flat-file data sources: save (AJAX) ───────────────────────────────────
    public async Task<IActionResult> OnPostSaveFlatFileAsync()
    {
        if (string.IsNullOrWhiteSpace(FlatFile?.Label))
            return new JsonResult(new { success = false, error = "Label is required." });

        // Multi-tenancy: uploads must belong to a tenant. Block SuperAdmin /
        // unauthenticated callers from writing into a shared/global folder.
        if (TenantId == Guid.Empty)
            return new JsonResult(new { success = false, error = "A tenant context is required to upload data sources." })
                { StatusCode = 400 };

        var isNewFlatFile = string.IsNullOrWhiteSpace(FlatFile.Id);

        // Enforce subscription database/data-source limit only when adding a new source.
        if (isNewFlatFile)
        {
            if (!await subscriptionLimits.CanAddDatabaseAsync(TenantId))
                return new JsonResult(new { success = false, error = "Your subscription plan's database/data source limit has been reached. Please upgrade your plan to add more data sources." });
        }

        try
        {
            var isNew = isNewFlatFile;
            var id    = isNew
                ? (FlatFile.Label.Trim().ToLowerInvariant()
                       .Replace(' ', '-').Replace('/', '-').Replace('\\', '-')
                   + "-" + Guid.NewGuid().ToString("N")[..6])
                : FlatFile.Id!.Trim();

            // ── Resolve the stored file path ──────────────────────────────────
            string filePath;

            if (FlatFileUpload is { Length: > 0 })
            {
                // Save uploaded file to {ContentRoot}/DataSources/{TenantId}/
                // so each tenant's files are isolated on disk.
                var dir = Path.Combine(
                    env.ContentRootPath,
                    "DataSources",
                    TenantId.ToString("N"));
                Directory.CreateDirectory(dir);

                var ext      = Path.GetExtension(FlatFileUpload.FileName);
                var safeFile = id + ext;
                filePath     = Path.Combine(dir, safeFile);

                // Remove any previous file for this ID with a different extension
                // (scoped to this tenant's folder only).
                foreach (var old in Directory.GetFiles(dir, id + ".*"))
                    if (!old.Equals(filePath, StringComparison.OrdinalIgnoreCase))
                        System.IO.File.Delete(old);

                await using var fs = new FileStream(filePath, FileMode.Create, FileAccess.Write);
                await FlatFileUpload.CopyToAsync(fs, HttpContext.RequestAborted);
            }
            else if (!isNew)
            {
                // Editing — keep the existing stored path
                var existing = (await settingsService.GetAsync(TenantId)).FlatFileSources
                    .FirstOrDefault(s => s.Id == id);
                filePath = existing?.FilePath ?? "";
                if (string.IsNullOrEmpty(filePath))
                    return new JsonResult(new { success = false, error = "No file found — please upload a file." });
            }
            else
            {
                return new JsonResult(new { success = false, error = "Please upload a file." });
            }

            var src = new FlatFileDataSource
            {
                Id                = id,
                Label             = FlatFile.Label.Trim(),
                Format            = FlatFile.Format,
                FilePath          = filePath,
                IsEnabled         = FlatFile.IsEnabled,
                HasHeaders        = FlatFile.HasHeaders,
                Delimiter         = string.IsNullOrWhiteSpace(FlatFile.Delimiter) ? null : FlatFile.Delimiter.Trim(),
                SheetName         = string.IsNullOrWhiteSpace(FlatFile.SheetName) ? null : FlatFile.SheetName.Trim(),
                Encoding          = string.IsNullOrWhiteSpace(FlatFile.Encoding)  ? "UTF-8" : FlatFile.Encoding.Trim(),
                MaxRows           = FlatFile.MaxRows > 0 ? FlatFile.MaxRows : 1000,
                FixedWidthColumns = ParseFixedWidthSpec(FlatFile.FixedWidthSpec),
            };

            await settingsService.AddOrUpdateFlatFileSourceAsync(TenantId, src);
            return new JsonResult(new
            {
                success  = true,
                message  = $"Data source \"{src.Label}\" {(isNew ? "added" : "updated")}.",
                id       = src.Id,
                fileName = Path.GetFileName(filePath),
            });
        }
        catch (Exception ex)
        {
            return new JsonResult(new { success = false, error = $"Save failed: {ex.Message}" })
            { StatusCode = 500 };
        }
    }

    // ── Flat-file data sources: delete ────────────────────────────────────────
    public async Task<IActionResult> OnPostDeleteFlatFileAsync(string id)
    {
        // Look up the source first so we can also remove its file from disk.
        var existing = (await settingsService.GetAsync(TenantId)).FlatFileSources
            .FirstOrDefault(s => s.Id == id);

        await settingsService.DeleteFlatFileSourceAsync(TenantId, id);

        // Best-effort delete of the on-disk file, scoped to this tenant's folder.
        if (existing is { FilePath: { Length: > 0 } storedPath })
        {
            try
            {
                var tenantDir = Path.GetFullPath(Path.Combine(
                    env.ContentRootPath,
                    "DataSources",
                    TenantId.ToString("N")));

                var fullPath = Path.GetFullPath(storedPath);

                // Only delete if the file actually lives inside this tenant's folder.
                if (fullPath.StartsWith(tenantDir + Path.DirectorySeparatorChar,
                                        StringComparison.OrdinalIgnoreCase)
                    && System.IO.File.Exists(fullPath))
                {
                    System.IO.File.Delete(fullPath);
                }
            }
            catch
            {
                // Ignore filesystem errors — DB row is already gone.
            }
        }

        TempData["FlatFileSuccess"] = "Data source removed.";
        return RedirectToPage();
    }

    // ── Flat-file data sources: toggle ────────────────────────────────────────
    public async Task<IActionResult> OnPostToggleFlatFileAsync(string id)
    {
        await settingsService.ToggleFlatFileSourceAsync(TenantId, id);
        return RedirectToPage();
    }

    // ── Flat-file data sources: preview (AJAX GET) ────────────────────────────
    public async Task<IActionResult> OnGetPreviewFlatFileAsync(string id)
    {
        var sp  = HttpContext.RequestServices;
        var svc = sp.GetRequiredService<FlatFileDataService>();

        var tenantCtx = sp.GetRequiredService<ITenantContext>();
        var (rows, error) = await svc.ReadAsync(id, tenantCtx.TenantId ?? Guid.Empty, maxRows: 10, ct: HttpContext.RequestAborted);
        if (error is not null)
            return new JsonResult(new { success = false, error });

        return new JsonResult(new
        {
            success = true,
            rowCount= rows.Count,
            columns = rows.Count > 0 ? rows[0].Keys.ToArray() : Array.Empty<string>(),
            rows,
        });
    }

    // ── Helpers ───────────────────────────────────────────────────────────────
    private static List<string> ParseFixedWidthSpec(string? spec)
    {
        if (string.IsNullOrWhiteSpace(spec)) return [];
        return spec.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                   .Where(line => !string.IsNullOrWhiteSpace(line))
                   .ToList();
    }

    // ── Memory: add or update (AJAX) ──────────────────────────────────────────
    public async Task<IActionResult> OnPostSaveMemoryAsync()
    {
        if (string.IsNullOrWhiteSpace(MemInput?.Content))
            return new JsonResult(new { success = false, error = "Content is required." });

        try
        {
            if (string.IsNullOrWhiteSpace(MemInput.Id))
            {
                await memoryService.AddManualAsync(
                    UserId, MemInput.Content, MemInput.Category?.Trim(), MemInput.Importance);
            }
            else if (!int.TryParse(MemInput.Id, out var memId) ||
                     !await memoryService.UpdateAsync(
                         memId, UserId, MemInput.Content, MemInput.Category?.Trim(), MemInput.Importance))
            {
                return new JsonResult(new { success = false, error = "Memory not found." });
            }

            return new JsonResult(new { success = true });
        }
        catch (Exception ex)
        {
            return new JsonResult(new { success = false, error = ex.Message }) { StatusCode = 500 };
        }
    }

    // ── Memory: delete (AJAX) ─────────────────────────────────────────────────
    public async Task<IActionResult> OnPostDeleteMemoryAsync(int id)
    {
        await memoryService.DeleteAsync(id, UserId);
        return new JsonResult(new { success = true });
    }

    // ── Memory: bulk delete (AJAX) ────────────────────────────────────────────
    public async Task<IActionResult> OnPostBulkDeleteMemoryAsync(string period)
    {
        DateTime? since = period switch
        {
            "30d"       => DateTime.UtcNow.AddDays(-30),
            "15d"       => DateTime.UtcNow.AddDays(-15),
            "7d"        => DateTime.UtcNow.AddDays(-7),
            "yesterday" => DateTime.UtcNow.Date.AddDays(-1),
            _           => (DateTime?)null   // "all"
        };

        var count = await memoryService.DeleteBulkAsync(UserId, since);
        return new JsonResult(new
        {
            success = true,
            count,
            message = count == 0
                ? "No matching memories found."
                : $"Deleted {count} memory item{(count == 1 ? "" : "s")}."
        });
    }

    // ── RAG: re-embed (retry embedding without re-uploading) ──────────────────
    public async Task<IActionResult> OnPostReembedRagAsync(string id)
    {
        var doc = (await settingsService.GetAsync(TenantId)).RagDocuments.FirstOrDefault(d => d.Id == id);
        if (doc is null)
            return new JsonResult(new { success = false, error = "Document not found." });

        try
        {
            var (chunkCount, embedded, error) = await ragService.ReembedAsync(id, TenantId);

            if (error is not null)
                return new JsonResult(new
                {
                    success = false,
                    error   = $"Re-embedding failed: {error}"
                });

            // Persist updated chunk count
            doc.ChunkCount = chunkCount;
            doc.UpdatedAt  = DateTime.UtcNow;
            await settingsService.AddOrUpdateRagDocumentAsync(TenantId, doc);

            return new JsonResult(new
            {
                success = true,
                message = $"Re-embedded \"{doc.Name}\" — {embedded}/{chunkCount} chunk(s) now have embeddings."
            });
        }
        catch (Exception ex)
        {
            return new JsonResult(new { success = false, error = ex.Message });
        }
    }

    // ── RAG: load saved content for the edit modal (AJAX GET) ────────────────
    public async Task<IActionResult> OnGetRagContentAsync(string id)
    {
        var contentPath = ragService.GetContentPath(TenantId, id);

        if (!System.IO.File.Exists(contentPath))
            return new JsonResult(new { content = (string?)null, found = false });

        var text = await System.IO.File.ReadAllTextAsync(contentPath);
        return new JsonResult(new { content = text, found = true });
    }

    // ── RAG: embedding health check (AJAX GET) ────────────────────────────────
    public IActionResult OnGetRagStatus(string id)
    {
        var chunksFile = ragService.GetChunksPath(TenantId, id);

        if (!System.IO.File.Exists(chunksFile))
            return new JsonResult(new { total = 0, embedded = 0 });

        try
        {
            var json   = System.IO.File.ReadAllText(chunksFile);
            var chunks = System.Text.Json.JsonSerializer.Deserialize<List<RagChunk>>(json) ?? [];
            var embedded = chunks.Count(c => c.Embedding.Length > 0);
            return new JsonResult(new { total = chunks.Count, embedded });
        }
        catch
        {
            return new JsonResult(new { total = 0, embedded = 0 });
        }
    }

    // ── Helpers ───────────────────────────────────────────────────────────────
    private void RemoveModelStatePrefix(string prefix)
    {
        var keys = ModelState.Keys
            .Where(k => k == prefix || k.StartsWith(prefix + "."))
            .ToList();
        foreach (var key in keys) ModelState.Remove(key);
    }

    // ── Logo: upload ──────────────────────────────────────────────────────────
    public async Task<IActionResult> OnPostUploadLogoAsync()
    {
        if (LogoUpload is null || LogoUpload.Length == 0)
        {
            TempData["LogoError"] = "Please select a PNG file to upload.";
            return RedirectToPage();
        }

        if (LogoUpload.Length > 256 * 1024)
        {
            TempData["LogoError"] = "File exceeds the 256 KB limit.";
            return RedirectToPage();
        }

        var header = new byte[24];
        await using (var vs = LogoUpload.OpenReadStream())
        {
            var read = await vs.ReadAsync(header.AsMemory(0, 24));
            if (read < 24)
            {
                TempData["LogoError"] = "File is too small to be a valid PNG.";
                return RedirectToPage();
            }
        }

        var dims = ReadPngDimensions(header);
        if (dims is null)
        {
            TempData["LogoError"] = "File is not a valid PNG.";
            return RedirectToPage();
        }

        const int RequiredWidth = 200, RequiredHeight = 50;
        if (dims.Value.Width != RequiredWidth || dims.Value.Height != RequiredHeight)
        {
            TempData["LogoError"] =
                $"Image must be exactly {RequiredWidth} × {RequiredHeight} px " +
                $"(uploaded image is {dims.Value.Width} × {dims.Value.Height} px).";
            return RedirectToPage();
        }

        // Multi-tenancy: each tenant's logo is stored under its own subfolder so
        // tenants don't overwrite each other's files.
        if (TenantId == Guid.Empty)
        {
            TempData["LogoError"] = "A tenant context is required to upload a logo.";
            return RedirectToPage();
        }

        var imagesDir = Path.Combine(env.WebRootPath, "images", "tenants", TenantId.ToString("N"));
        Directory.CreateDirectory(imagesDir);

        await using (var fs  = new FileStream(Path.Combine(imagesDir, "app-logo.png"), FileMode.Create, FileAccess.Write))
        await using (var src = LogoUpload.OpenReadStream())
            await src.CopyToAsync(fs, HttpContext.RequestAborted);

        await settingsService.SaveLogoAsync(TenantId, "app-logo.png");
        TempData["LogoSuccess"] = "Logo uploaded successfully.";
        return RedirectToPage();
    }

    // ── Logo: delete ──────────────────────────────────────────────────────────
    public async Task<IActionResult> OnPostDeleteLogoAsync()
    {
        // Delete the per-tenant logo file (if any). Falls back to the legacy
        // wwwroot/images/app-logo.png path for older uploads.
        var tenantPath = TenantId == Guid.Empty
            ? null
            : Path.Combine(env.WebRootPath, "images", "tenants", TenantId.ToString("N"), "app-logo.png");
        var legacyPath = Path.Combine(env.WebRootPath, "images", "app-logo.png");

        if (tenantPath is not null && System.IO.File.Exists(tenantPath))
            System.IO.File.Delete(tenantPath);
        else if (System.IO.File.Exists(legacyPath))
            System.IO.File.Delete(legacyPath);

        await settingsService.SaveLogoAsync(TenantId, null);
        TempData["LogoSuccess"] = "Custom logo removed. Default logo restored.";
        return RedirectToPage();
    }

    /// <summary>
    /// Reads width and height from a PNG IHDR chunk without an image library.
    /// Expects the first 24 bytes of the file (signature + IHDR length + type + dimensions).
    /// </summary>
    private static (int Width, int Height)? ReadPngDimensions(ReadOnlySpan<byte> header)
    {
        if (header.Length < 24) return null;
        ReadOnlySpan<byte> pngSig = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];
        if (!header[..8].SequenceEqual(pngSig)) return null;
        var width  = (header[16] << 24) | (header[17] << 16) | (header[18] << 8) | header[19];
        var height = (header[20] << 24) | (header[21] << 16) | (header[22] << 8) | header[23];
        return (width, height);
    }

    // ── Terminal ──────────────────────────────────────────────────────────────
    public async Task<IActionResult> OnPostSaveTerminalAsync()
    {
        var model = new TerminalSettings
        {
            IsEnabled        = Terminal.IsEnabled,
            DefaultShell     = Terminal.DefaultShell,
            WorkingDirectory = Terminal.WorkingDirectory?.Trim() ?? "",
            TimeoutSeconds   = Math.Clamp(Terminal.TimeoutSeconds,  5, 300),
            MaxOutputLength  = Math.Clamp(Terminal.MaxOutputLength, 1000, 100_000),
        };
        await settingsService.SaveTerminalSettingsAsync(TenantId, model);
        TempData["TerminalSuccess"] = "Terminal settings saved.";
        return RedirectToPage();
    }

    // ── AgentMail ─────────────────────────────────────────────────────────────
    public async Task<IActionResult> OnPostSaveAgentMailAsync()
    {
        // AgentMail is global outbound email infrastructure — only SuperAdmin may modify it.
        if (!User.IsInRole("SuperAdmin"))
            return Forbid();

        // Preserve fields that aren't surfaced in this UI (FromAddress / FromName).
        var existing = await globalAgentMail.GetAsync();
        var model = new GlobalAgentMailSettings
        {
            Id           = 1,
            IsEnabled    = AgentMail.IsEnabled,
            ApiKey       = AgentMail.ApiKey?.Trim() ?? "",
            ApiBaseUrl   = string.IsNullOrWhiteSpace(AgentMail.BaseUrl)
                               ? "https://api.agentmail.to/v0"
                               : AgentMail.BaseUrl.Trim(),
            DefaultInbox = AgentMail.DefaultInbox?.Trim() ?? "",
            FromAddress  = existing.FromAddress,
            FromName     = existing.FromName,
        };
        await globalAgentMail.SaveAsync(model);
        TempData["AgentMailSuccess"] = "AgentMail settings saved.";
        return RedirectToPage();
    }

    // ── Security ────────────────────────────────────────────────────────────
    public async Task<IActionResult> OnPostSaveSecurityAsync()
    {
        // Security policy is global authentication infrastructure — only SuperAdmin may modify it.
        if (!User.IsInRole("SuperAdmin"))
            return Forbid();

        var model = new Pulse.Models.SecuritySettings
        {
            MaxFailedLoginAttempts = Math.Clamp(Security.MaxFailedLoginAttempts, 1, 20),
            LoginLockoutHours      = Math.Clamp(Security.LoginLockoutHours,      1, 168),
        };
        await settingsService.SaveSecuritySettingsAsync(TenantId, model);
        TempData["SecuritySuccess"] = "Security settings saved.";
        return RedirectToPage();
    }

    // ── App name ─────────────────────────────────────────────────────────────
    public async Task<IActionResult> OnPostSaveAppNameAsync()
    {
        var name = AppName?.Trim();
        if (string.IsNullOrWhiteSpace(name)) name = "Pulse";
        await settingsService.SaveAppNameAsync(TenantId, name);
        TempData["AppNameSuccess"] = $"App name saved as \"{name}\"."; 
        return RedirectToPage();
    }

    // ── Input models ──────────────────────────────────────────────────────
    public class LlmInput
    {
        [Required(ErrorMessage = "Model ID is required.")]
        public string ModelId { get; set; } = "";

        public List<string> ApiKeys { get; set; } = [];
    }

    public class McpInput
    {
        public string Id { get; set; } = "";

        [Required(ErrorMessage = "Name is required.")]
        [MaxLength(80)]
        public string Name { get; set; } = "";

        [Required]
        public string TransportType { get; set; } = "http";

        public string? Url { get; set; }
        public string? Command { get; set; }
        public string? Arguments { get; set; }
        public bool IsEnabled { get; set; } = true;
    }

    public class SkillInput
    {
        public string Id { get; set; } = "";

        [Required(ErrorMessage = "Name is required.")]
        [MaxLength(80)]
        public string Name { get; set; } = "";

        [MaxLength(10)]
        public string Icon { get; set; } = "⚡";

        [MaxLength(300)]
        public string Description { get; set; } = "";

        [Required(ErrorMessage = "Instructions are required.")]
        public string Instructions { get; set; } = "";

        public bool IsActive { get; set; } = true;
    }

    public class RagDocInput
    {
        public string Id { get; set; } = "";

        [Required(ErrorMessage = "Name is required.")]
        [MaxLength(80)]
        public string Name { get; set; } = "";

        [MaxLength(300)]
        public string Description { get; set; } = "";

        /// <summary>"text" or "file"</summary>
        public string SourceType { get; set; } = "text";

        /// <summary>Text content when SourceType is "text".</summary>
        public string? Content { get; set; }

        public bool IsEnabled { get; set; } = true;
    }

    public class MemorySettingsInput
    {
        public string? Id { get; set; }

        [Required(ErrorMessage = "Content is required.")]
        [MaxLength(2000)]
        public string Content { get; set; } = "";

        [MaxLength(60)]
        public string? Category { get; set; }

        [Range(1, 5)]
        public int Importance { get; set; } = 3;
    }

    public class DbConnInput
    {
        /// <summary>Logical key used by the agent (e.g. "sales-db"). Lowercase, no spaces.</summary>
        [Required(ErrorMessage = "Connection ID is required.")]
        [MaxLength(60)]
        public string ConnectionId { get; set; } = "";

        [MaxLength(120)]
        public string Label { get; set; } = "";

        public DatabaseProvider Provider { get; set; } = DatabaseProvider.SqlServer;

        [Required(ErrorMessage = "Connection string is required.")]
        public string ConnectionString { get; set; } = "";

        public bool IsEnabled { get; set; } = true;
        public bool ReadOnly  { get; set; } = true;

        [Range(1, 10000)]
        public int MaxRows { get; set; } = 500;

        /// <summary>Comma-separated schema names (UI convenience).</summary>
        public string? AllowedSchemas { get; set; }
    }

    public class TerminalInput
    {
        public bool   IsEnabled        { get; set; } = false;
        public string DefaultShell     { get; set; } = "powershell";
        public string WorkingDirectory { get; set; } = "";
        [Range(5, 300)]  public int TimeoutSeconds  { get; set; } = 30;
        [Range(1000, 100000)] public int MaxOutputLength { get; set; } = 8000;
    }

    public class AgentMailInput
    {
        public bool   IsEnabled    { get; set; } = false;
        public string ApiKey       { get; set; } = "";
        public string BaseUrl      { get; set; } = "https://api.agentmail.to/v0";
        public string DefaultInbox { get; set; } = "";
    }

    public class SecurityInput
    {
        [Range(1, 20, ErrorMessage = "Must be between 1 and 20.")]
        public int MaxFailedLoginAttempts { get; set; } = 5;

        [Range(1, 168, ErrorMessage = "Must be between 1 and 168 (1 week).")]
        public int LoginLockoutHours { get; set; } = 1;
    }

    public class FlatFileInput
    {
        public string?        Id              { get; set; }
        public string         Label           { get; set; } = "";
        public FlatFileFormat Format          { get; set; } = FlatFileFormat.Csv;
        public bool           IsEnabled       { get; set; } = true;
        public bool           HasHeaders      { get; set; } = true;
        public string?        Delimiter       { get; set; }
        public string?        SheetName       { get; set; }
        public string         Encoding        { get; set; } = "UTF-8";
        public int            MaxRows         { get; set; } = 1000;
        public string?        FixedWidthSpec  { get; set; }
    }

    public class AgentInput
    {
        public string Id { get; set; } = "";

        [Required(ErrorMessage = "Name is required.")]
        [MaxLength(80)]
        public string Name { get; set; } = "";

        [MaxLength(10)]
        public string Icon { get; set; } = "🤖";

        [MaxLength(300)]
        public string Description { get; set; } = "";

        [Required(ErrorMessage = "System prompt is required.")]
        public string SystemPrompt { get; set; } = "";

        public string? ModelId { get; set; }

        public bool IsEnabled      { get; set; } = true;
        public bool IsOrchestrator { get; set; } = false;

        [Range(0, 999)]
        public int SortOrder { get; set; } = 0;

        public List<string> AllowedPluginKeys     { get; set; } = [];
        public List<string> AllowedSkillIds       { get; set; } = [];
        public List<string> AllowedMcpServerIds   { get; set; } = [];
        public List<string> AllowedDatabaseKeys   { get; set; } = [];
        public List<string> AllowedFlatFileIds    { get; set; } = [];
        public List<string> AllowedRagDocumentIds { get; set; } = [];
    }
}


