# OpenRouter LLM Settings Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Replace Google Gemini with OpenRouter as the sole LLM provider — removing the Gemini connector, Gemini-specific settings, and key-rotation service, and replacing the LLM tab with an OpenRouter API key field plus a live model picker with cost display and filtering.

**Architecture:** A new `OpenRouterService` replaces `GeminiKeyRotationService` and is injected into the three agent runner services. A new `OnGetOpenRouterModelsAsync` page handler fetches the model list from `https://openrouter.ai/api/v1/models`, caches it server-side for 1 hour, and returns JSON to the browser. All model filtering happens client-side in JS.

**Tech Stack:** ASP.NET Core 10 Razor Pages, Semantic Kernel 1.74 (OpenAI connector, `SKEXP0010`), Entity Framework Core 10, Bootstrap 5, vanilla JS with 500ms debounce.

---

## File Map

| Action | Path |
|--------|------|
| **Delete** | `Pulse/Services/GeminiKeyRotationService.cs` |
| **Create** | `Pulse/Services/OpenRouterService.cs` |
| **Modify** | `Pulse/Pulse.csproj` |
| **Modify** | `Pulse/Data/Entities/GlobalLlmSettings.cs` |
| **Modify** | `Pulse/Data/Entities/AppSettingsEntity.cs` |
| **Modify** | `Pulse/Models/LlmSettingsModel.cs` |
| **Modify** | `Pulse/Services/LlmSettingsService.cs` |
| **Modify** | `Pulse/Services/GlobalLlmSettingsService.cs` |
| **Modify** | `Pulse/Services/AgentOrchestrationService.cs` |
| **Modify** | `Pulse/Services/SpecializedAgentRunner.cs` |
| **Modify** | `Pulse/Services/ScheduledAgentRunner.cs` |
| **Modify** | `Pulse/Program.cs` |
| **Modify** | `Pulse/Pages/Settings.cshtml.cs` |
| **Modify** | `Pulse/Pages/Settings.cshtml` |
| **Create** | EF migration (via `dotnet ef migrations add`) |

---

## Task 1: Remove Gemini NuGet Package and Update Data Models

**Files:**
- Modify: `Pulse/Pulse.csproj`
- Modify: `Pulse/Data/Entities/GlobalLlmSettings.cs`
- Modify: `Pulse/Data/Entities/AppSettingsEntity.cs`
- Modify: `Pulse/Models/LlmSettingsModel.cs`

- [ ] **Step 1: Remove the Google SK connector from the csproj**

In `Pulse/Pulse.csproj`, delete this line:
```xml
<PackageReference Include="Microsoft.SemanticKernel.Connectors.Google" Version="1.74.0-alpha" />
```
`Microsoft.SemanticKernel` 1.74.0 already includes the OpenAI connector transitively — no new package reference needed.

- [ ] **Step 2: Remove `ApiVersion` from `GlobalLlmSettings`**

Replace the entire file `Pulse/Data/Entities/GlobalLlmSettings.cs` with:
```csharp
namespace Pulse.Data.Entities;

/// <summary>
/// Global LLM configuration managed exclusively by SuperAdmin. Every tenant uses the
/// <see cref="ModelId"/> stored in this singleton row.
/// Singleton row enforced by CHECK constraint (Id = 1).
/// </summary>
public class GlobalLlmSettings
{
    public int Id { get; set; } = 1;

    /// <summary>
    /// OpenRouter model identifier (e.g. <c>openai/gpt-4o</c>, <c>anthropic/claude-3-5-sonnet</c>).
    /// </summary>
    public string ModelId { get; set; } = string.Empty;
}
```

- [ ] **Step 3: Remove `ApiVersion` from `AppSettingsEntity`**

Replace the entire file `Pulse/Data/Entities/AppSettingsEntity.cs` with:
```csharp
using Pulse.Models;

namespace Pulse.Data.Entities;

/// <summary>
/// Per-tenant settings row holding scalar + owned-type settings.
/// Collections (Skills, McpServers, RagDocuments, FlatFileSources, DatabaseConnections, ApiKeys)
/// are stored in their own tables.
/// </summary>
public class AppSettingsEntity : ITenantOwned
{
    public int Id { get; set; }

    public Guid TenantId { get; set; }

    public string AppName { get; set; } = "Pulse";
    public string ModelId { get; set; } = "";

    public string? LogoFileName { get; set; }
    public string? LogoVersion  { get; set; }

    public TerminalSettings Terminal  { get; set; } = new();
    public SecuritySettings Security  { get; set; } = new();

    public DateTime UpdatedAtUtc { get; set; } = DateTime.UtcNow;
}
```

- [ ] **Step 4: Remove `ApiVersion` from `LlmSettingsModel`**

In `Pulse/Models/LlmSettingsModel.cs`, delete these two lines:
```csharp
    /// <summary>Google Gemini API version: "V1Beta" (default) or "V1".</summary>
    public string ApiVersion { get; set; } = "V1Beta";
```

- [ ] **Step 5: Build to confirm no missing-member errors yet**

Run:
```
dotnet build Pulse/Pulse.csproj
```
Expected: Several errors referencing `ApiVersion` and `GeminiKeyRotationService` — that is normal at this stage. Confirm no *other* unexpected errors.

- [ ] **Step 6: Commit**
```
git add Pulse/Pulse.csproj Pulse/Data/Entities/GlobalLlmSettings.cs Pulse/Data/Entities/AppSettingsEntity.cs Pulse/Models/LlmSettingsModel.cs
git commit -m "chore: remove Gemini NuGet package and ApiVersion from data models"
```

---

## Task 2: Create OpenRouterService

**Files:**
- Create: `Pulse/Services/OpenRouterService.cs`

- [ ] **Step 1: Create the service**

Create `Pulse/Services/OpenRouterService.cs`:
```csharp
namespace Pulse.Services;

/// <summary>
/// Provides the active OpenRouter API key for agent kernel construction.
/// Replaces the former GeminiKeyRotationService — OpenRouter handles load-balancing
/// on their end so no client-side key rotation is needed.
/// </summary>
public sealed class OpenRouterService
{
    public const string BaseUrl = "https://openrouter.ai/api/v1";

    /// <summary>
    /// Returns the first non-empty key from <paramref name="keys"/>.
    /// Throws <see cref="InvalidOperationException"/> when the list is empty or all entries are blank.
    /// </summary>
    public string GetApiKey(IReadOnlyList<string> keys)
    {
        var key = keys.FirstOrDefault(k => !string.IsNullOrWhiteSpace(k));
        if (key is null)
            throw new InvalidOperationException(
                "No OpenRouter API key is configured. Add one in Settings → LLM.");
        return key;
    }
}
```

- [ ] **Step 2: Commit**
```
git add Pulse/Services/OpenRouterService.cs
git commit -m "feat: add OpenRouterService replacing GeminiKeyRotationService"
```

---

## Task 3: Update LlmSettingsService and GlobalLlmSettingsService

**Files:**
- Modify: `Pulse/Services/LlmSettingsService.cs`
- Modify: `Pulse/Services/GlobalLlmSettingsService.cs`

- [ ] **Step 1: Remove `ApiVersion` from `GlobalLlmSettingsService.SaveAsync`**

In `Pulse/Services/GlobalLlmSettingsService.cs`, replace the `SaveAsync` method body:
```csharp
    public async Task SaveAsync(GlobalLlmSettings settings)
    {
        await using var db = await _factory.CreateDbContextAsync();
        var existing = await db.GlobalLlmSettings.FindAsync(1);
        if (existing is null)
        {
            settings.Id = 1;
            db.GlobalLlmSettings.Add(settings);
        }
        else
        {
            existing.ModelId = settings.ModelId;
        }
        await db.SaveChangesAsync();
        _cache.Remove(CacheKey);
    }
```

- [ ] **Step 2: Remove `ApiVersion` shadowing from `LlmSettingsService.GetAsync`**

In `Pulse/Services/LlmSettingsService.cs`, replace the end of `GetAsync` where it shadows global values:
```csharp
        // Global model shadows any per-tenant row value.
        var global = await _globalLlm.GetAsync();
        model.ModelId = global.ModelId ?? string.Empty;
        return model;
```
(Delete the `model.ApiVersion = ...` line.)

- [ ] **Step 3: Remove `ApiVersion` from `ApplyScalars`**

In `Pulse/Services/LlmSettingsService.cs`, in the `ApplyScalars` static method, delete:
```csharp
        row.ApiVersion   = string.IsNullOrWhiteSpace(m.ApiVersion) ? "V1Beta" : m.ApiVersion;
```

- [ ] **Step 4: Remove `ApiVersion` from `LoadFromDbAsync`**

In `Pulse/Services/LlmSettingsService.cs`, in `LoadFromDbAsync`, change:
```csharp
                ModelId          = row?.ModelId      ?? "",
                ApiVersion       = string.IsNullOrWhiteSpace(row?.ApiVersion) ? "V1Beta" : row!.ApiVersion,
```
to:
```csharp
                ModelId          = row?.ModelId      ?? "",
```

- [ ] **Step 5: Commit**
```
git add Pulse/Services/LlmSettingsService.cs Pulse/Services/GlobalLlmSettingsService.cs
git commit -m "chore: remove ApiVersion from settings services"
```

---

## Task 4: Run EF Migration

**Files:**
- Create: EF migration (auto-generated)

- [ ] **Step 1: Add the migration**

From the repo root, run:
```
dotnet ef migrations add ReplaceGeminiWithOpenRouter --project Pulse --startup-project Pulse
```
This generates a new file under `Pulse/Data/Migrations/`.

- [ ] **Step 2: Verify the generated migration**

Open the generated `..._ReplaceGeminiWithOpenRouter.cs` file and confirm it contains `DropColumn` calls for `ApiVersion` on both `AppSettings` and `GlobalLlmSettings` tables. If the migration is empty or wrong, check that the entity changes from Task 1 Steps 2–4 were saved correctly.

- [ ] **Step 3: Apply migration locally**

```
dotnet ef database update --project Pulse --startup-project Pulse
```
Expected: Migration applies without error.

- [ ] **Step 4: Commit**
```
git add Pulse/Data/Migrations/
git commit -m "feat: EF migration to drop ApiVersion columns (Gemini → OpenRouter)"
```

---

## Task 5: Update Agent Runners

**Files:**
- Modify: `Pulse/Services/AgentOrchestrationService.cs`
- Modify: `Pulse/Services/SpecializedAgentRunner.cs`
- Modify: `Pulse/Services/ScheduledAgentRunner.cs`
- Delete: `Pulse/Services/GeminiKeyRotationService.cs`

### 5a — AgentOrchestrationService

- [ ] **Step 1: Replace usings at the top of `AgentOrchestrationService.cs`**

Remove:
```csharp
using Microsoft.SemanticKernel.Connectors.Google;
```
Add:
```csharp
using Microsoft.SemanticKernel.Connectors.OpenAI;
```

- [ ] **Step 2: Replace the constructor parameter `GeminiKeyRotationService keyRotation` with `OpenRouterService openRouter`**

Change:
```csharp
public sealed class AgentOrchestrationService(
    GeminiKeyRotationService keyRotation,
```
to:
```csharp
public sealed class AgentOrchestrationService(
    OpenRouterService openRouter,
```

- [ ] **Step 3: Delete the `ParseApiVersion` helper method**

Remove:
```csharp
    private static GoogleAIVersion ParseApiVersion(string? value) =>
        string.Equals(value, "V1", StringComparison.OrdinalIgnoreCase)
            ? GoogleAIVersion.V1
            : GoogleAIVersion.V1_Beta;
```

- [ ] **Step 4: Replace the kernel construction in the retry loop**

Find:
```csharp
                await keyRotation.EnforceRateLimitAsync(cancellationToken);
                var apiKey = keyRotation.GetNextKey(settings.ApiKeys);

                var kernelBuilder = Kernel.CreateBuilder()
                    .AddGoogleAIGeminiChatCompletion(modelId, apiKey, apiVersion: ParseApiVersion(settings.ApiVersion));
                var kernel = kernelBuilder.Build();
```
Replace with:
```csharp
                var apiKey = openRouter.GetApiKey(settings.ApiKeys);

#pragma warning disable SKEXP0010
                var kernelBuilder = Kernel.CreateBuilder()
                    .AddOpenAIChatCompletion(modelId, apiKey, endpoint: new Uri(OpenRouterService.BaseUrl));
#pragma warning restore SKEXP0010
                var kernel = kernelBuilder.Build();
```

- [ ] **Step 5: Replace `GeminiPromptExecutionSettings` with `OpenAIPromptExecutionSettings`**

Find:
```csharp
                var executionSettings = new GeminiPromptExecutionSettings
                {
                    MaxTokens = 8192,
                    Temperature = 0.7,
                    ToolCallBehavior = kernel.Plugins.Count > 0
                        ? GeminiToolCallBehavior.AutoInvokeKernelFunctions
                        : null
                };
```
Replace with:
```csharp
                var executionSettings = new OpenAIPromptExecutionSettings
                {
                    MaxTokens = 8192,
                    Temperature = 0.7,
                    ToolCallBehavior = kernel.Plugins.Count > 0
                        ? ToolCallBehavior.AutoInvokeKernelFunctions
                        : null
                };
```

### 5b — SpecializedAgentRunner

- [ ] **Step 6: Replace usings at the top of `SpecializedAgentRunner.cs`**

Remove:
```csharp
using Microsoft.SemanticKernel.Connectors.Google;
```
Add:
```csharp
using Microsoft.SemanticKernel.Connectors.OpenAI;
```

- [ ] **Step 7: Replace the constructor parameter in `SpecializedAgentRunner`**

Change:
```csharp
public sealed class SpecializedAgentRunner(
    GeminiKeyRotationService keyRotation,
```
to:
```csharp
public sealed class SpecializedAgentRunner(
    OpenRouterService openRouter,
```

- [ ] **Step 8: Replace the kernel construction in `SpecializedAgentRunner`**

Find:
```csharp
            await keyRotation.EnforceRateLimitAsync(ct);
            var apiKey = keyRotation.GetNextKey(settings.ApiKeys);

            // Always use the global LLM Configuration model. Per-agent ModelId overrides
            // are intentionally ignored — Settings → LLM → Model ID is the single source of truth.
            var modelId = settings.ModelId;

            var kernelBuilder = Kernel.CreateBuilder()
                .AddGoogleAIGeminiChatCompletion(modelId, apiKey, apiVersion: ParseApiVersion(settings.ApiVersion));
```
Replace with:
```csharp
            var apiKey = openRouter.GetApiKey(settings.ApiKeys);

            // Always use the global LLM Configuration model. Per-agent ModelId overrides
            // are intentionally ignored — Settings → LLM → Model ID is the single source of truth.
            var modelId = settings.ModelId;

#pragma warning disable SKEXP0010
            var kernelBuilder = Kernel.CreateBuilder()
                .AddOpenAIChatCompletion(modelId, apiKey, endpoint: new Uri(OpenRouterService.BaseUrl));
#pragma warning restore SKEXP0010
```

- [ ] **Step 9: Replace `GeminiPromptExecutionSettings` in `SpecializedAgentRunner`**

Find:
```csharp
            var execSettings = new GeminiPromptExecutionSettings
            {
                MaxTokens = 8192,
                Temperature = 0.7,
                ToolCallBehavior = kernel.Plugins.Count > 0
                    ? GeminiToolCallBehavior.AutoInvokeKernelFunctions
                    : null
            };
```
Replace with:
```csharp
            var execSettings = new OpenAIPromptExecutionSettings
            {
                MaxTokens = 8192,
                Temperature = 0.7,
                ToolCallBehavior = kernel.Plugins.Count > 0
                    ? ToolCallBehavior.AutoInvokeKernelFunctions
                    : null
            };
```

- [ ] **Step 10: Delete `ParseApiVersion` from `SpecializedAgentRunner`**

Remove:
```csharp
    private static GoogleAIVersion ParseApiVersion(string? value) =>
        string.Equals(value, "V1", StringComparison.OrdinalIgnoreCase)
            ? GoogleAIVersion.V1
            : GoogleAIVersion.V1_Beta;
```

### 5c — ScheduledAgentRunner

- [ ] **Step 11: Replace usings at the top of `ScheduledAgentRunner.cs`**

Remove:
```csharp
using Microsoft.SemanticKernel.Connectors.Google;
```
Add:
```csharp
using Microsoft.SemanticKernel.Connectors.OpenAI;
```

- [ ] **Step 12: Replace the constructor parameter in `ScheduledAgentRunner`**

Change:
```csharp
public sealed class ScheduledAgentRunner(
    GeminiKeyRotationService keyRotation,
```
to:
```csharp
public sealed class ScheduledAgentRunner(
    OpenRouterService openRouter,
```

- [ ] **Step 13: Replace the kernel construction in `ScheduledAgentRunner`**

Find:
```csharp
            await keyRotation.EnforceRateLimitAsync(ct);
            var apiKey = keyRotation.GetNextKey(settings.ApiKeys);

            var kernel = Kernel.CreateBuilder()
                .AddGoogleAIGeminiChatCompletion(settings.ModelId, apiKey)
                .Build();
```
Replace with:
```csharp
            var apiKey = openRouter.GetApiKey(settings.ApiKeys);

#pragma warning disable SKEXP0010
            var kernel = Kernel.CreateBuilder()
                .AddOpenAIChatCompletion(settings.ModelId, apiKey, endpoint: new Uri(OpenRouterService.BaseUrl))
                .Build();
#pragma warning restore SKEXP0010
```

- [ ] **Step 14: Replace `GeminiPromptExecutionSettings` in `ScheduledAgentRunner`**

Find:
```csharp
            var executionSettings = new GeminiPromptExecutionSettings
            {
                MaxTokens        = 4096,
                Temperature      = 0.3,
                ToolCallBehavior = kernel.Plugins.Count > 0
                    ? GeminiToolCallBehavior.AutoInvokeKernelFunctions
                    : null
            };
```
Replace with:
```csharp
            var executionSettings = new OpenAIPromptExecutionSettings
            {
                MaxTokens        = 4096,
                Temperature      = 0.3,
                ToolCallBehavior = kernel.Plugins.Count > 0
                    ? ToolCallBehavior.AutoInvokeKernelFunctions
                    : null
            };
```

- [ ] **Step 15: Delete `GeminiKeyRotationService.cs`**
```
git rm Pulse/Services/GeminiKeyRotationService.cs
```

- [ ] **Step 16: Build to confirm compile success**
```
dotnet build Pulse/Pulse.csproj
```
Expected: Build succeeds with 0 errors. There may be warnings about `SKEXP0010` if the pragma suppressions were missed — fix any remaining ones.

- [ ] **Step 17: Commit**
```
git add Pulse/Services/AgentOrchestrationService.cs Pulse/Services/SpecializedAgentRunner.cs Pulse/Services/ScheduledAgentRunner.cs
git commit -m "feat: swap Gemini SK connector for OpenRouter (OpenAI-compatible endpoint)"
```

---

## Task 6: Update Program.cs

**Files:**
- Modify: `Pulse/Program.cs`

- [ ] **Step 1: Replace `GeminiKeyRotationService` registration with `OpenRouterService`**

Find:
```csharp
builder.Services.AddSingleton<GeminiKeyRotationService>();
```
Replace with:
```csharp
builder.Services.AddSingleton<OpenRouterService>();
```

- [ ] **Step 2: Commit**
```
git add Pulse/Program.cs
git commit -m "chore: register OpenRouterService instead of GeminiKeyRotationService in DI"
```

---

## Task 7: Update Settings.cshtml.cs

**Files:**
- Modify: `Pulse/Pages/Settings.cshtml.cs`

- [ ] **Step 1: Add `System.Security.Cryptography` using at top of file**

At the top of `Settings.cshtml.cs`, add to the existing usings:
```csharp
using System.Security.Cryptography;
using System.Net.Http.Headers;
using System.Text.Json;
```

- [ ] **Step 2: Update `LlmInput` class (at the bottom of `Settings.cshtml.cs`)**

Find:
```csharp
    public class LlmInput
    {
        [Required(ErrorMessage = "Model ID is required.")]
        public string ModelId { get; set; } = "";

        \ <summary>Google Gemini API version: "V1Beta" or "V1".</summary>
        public string ApiVersion { get; set; } = "V1Beta";

        public List<string> ApiKeys { get; set; } = [];
    }
```
Replace with:
```csharp
    public class LlmInput
    {
        [Required(ErrorMessage = "Model ID is required.")]
        public string ModelId { get; set; } = "";

        public List<string> ApiKeys { get; set; } = [];
    }
```

- [ ] **Step 3: Update `OnGetAsync` — remove `ApiVersion` line**

Find:
```csharp
        Input.ModelId    = current.ModelId;
        Input.ApiVersion = string.IsNullOrWhiteSpace(current.ApiVersion) ? "V1Beta" : current.ApiVersion;
        Input.ApiKeys    = current.ApiKeys.Count > 0 ? [.. current.ApiKeys] : [""];
```
Replace with:
```csharp
        Input.ModelId = current.ModelId;
        Input.ApiKeys = current.ApiKeys.Count > 0 ? [.. current.ApiKeys] : [""];
```

- [ ] **Step 4: Update `OnPostAsync` — remove `ApiVersion` from the global save**

Find:
```csharp
        // ModelId + ApiVersion are stored globally and shared by every tenant.
        await globalLlm.SaveAsync(new Data.Entities.GlobalLlmSettings
        {
            ModelId    = Input.ModelId.Trim(),
            ApiVersion = string.IsNullOrWhiteSpace(Input.ApiVersion) ? "V1Beta" : Input.ApiVersion.Trim(),
        });
```
Replace with:
```csharp
        await globalLlm.SaveAsync(new Data.Entities.GlobalLlmSettings
        {
            ModelId = Input.ModelId.Trim(),
        });
```

- [ ] **Step 5: Update `OnPostAsync` — remove `ApiVersion` from the per-tenant save**

Find:
```csharp
            // ModelId / ApiVersion intentionally left blank — LlmSettingsService.GetAsync
            // shadows them with the global values managed by SuperAdmin.
            ModelId             = string.Empty,
            ApiVersion          = "V1Beta",
```
Replace with:
```csharp
            ModelId             = string.Empty,
```

- [ ] **Step 6: Add the `OnGetOpenRouterModelsAsync` handler**

Add this method inside `SettingsModel`, after `OnPostAsync` and before `OnPostSaveMcpAsync`:

```csharp
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
```

Also add `using Microsoft.Extensions.Caching.Memory;` to the usings at the top of the file if not already present.

- [ ] **Step 7: Build to confirm no errors**
```
dotnet build Pulse/Pulse.csproj
```
Expected: 0 errors.

- [ ] **Step 8: Commit**
```
git add Pulse/Pages/Settings.cshtml.cs
git commit -m "feat: add OnGetOpenRouterModelsAsync handler and clean up ApiVersion from settings page"
```

---

## Task 8: Update Settings.cshtml — LLM Tab UI

**Files:**
- Modify: `Pulse/Pages/Settings.cshtml`

- [ ] **Step 1: Replace the entire LLM tab pane content**

Find the block between the two comments (inclusive):
```html
<div id="tab-pane-llm" class="settings-tab-pane pt-4">
<form method="post" id="llmForm" novalidate>
    @Html.AntiForgeryToken()
    <div class="card shadow-sm mb-4">
        <div class="card-header bg-white py-3">
            <div class="settings-section-header">🤖 LLM Configuration</div>
        </div>
        <div class="card-body">

            <div class="mb-4">
                <label asp-for="Input.ModelId" class="form-label fw-semibold">Model ID</label>
                <input asp-for="Input.ModelId" class="form-control"
                       placeholder="e.g. gemini-2.0-flash" autocomplete="off" spellcheck="false" />
                <span asp-validation-for="Input.ModelId" class="text-danger small"></span>
                <div class="form-text">
                    Enter any Gemini model ID supported by your API key
                    (e.g. <code>gemini-2.0-flash</code>, <code>gemini-2.5-flash</code>, <code>gemini-2.5-pro</code>).
                    This value is sent to Google as-is.
                    <br />
                    <strong>Global setting:</strong> applies to every tenant.
                </div>
            </div>

            <div class="mb-4">
                <label asp-for="Input.ApiVersion" class="form-label fw-semibold">Gemini API Version</label>
                <select asp-for="Input.ApiVersion" class="form-select">
                    <option value="V1Beta">v1beta (default — most models, including previews)</option>
                    <option value="V1">v1 (stable GA endpoint)</option>
                </select>
                <div class="form-text">
                    Switch to <code>v1</code> only if Google explicitly states the model lives there.
                    Most preview / 3.x models are still served on <code>v1beta</code>.
                </div>
            </div>

            <hr class="my-3" />

            <div class="mb-2">
                <label class="form-label fw-semibold mb-0">API Keys</label>
                <div class="text-muted small mt-1 mb-3">
                    Keys rotate in round-robin after every call. A 5-second rate-limit gap is enforced automatically.<br />
                    <span class="info-badge mt-2">
                        ℹ️ Stored in <code>llm-settings.json</code>. Use User Secrets or Azure Key Vault for production.
                    </span>
                </div>
                <div id="keysContainer">
                    @for (int i = 0; i < Model.Input.ApiKeys.Count; i++)
                    {
                        <div class="key-row" data-index="@i">
                            <span class="key-num">#@(i + 1)</span>
                            <input type="password" name="Input.ApiKeys[@i]" value="@Model.Input.ApiKeys[i]"
                                   class="form-control" placeholder="AIza…" spellcheck="false" autocomplete="off" />
                            <button type="button" class="btn btn-outline-secondary btn-icon btn-toggle-vis" title="Show/hide">👁</button>
                            <button type="button" class="btn btn-outline-danger btn-icon btn-remove-key" title="Remove">✕</button>
                        </div>
                    }
                </div>
                <button type="button" id="addKeyBtn" class="btn btn-outline-primary btn-sm mt-2">➕ Add Key</button>
            </div>
        </div>
        <div class="card-footer bg-white border-top d-flex justify-content-end py-3">
            <button type="submit" class="btn btn-primary px-4" id="llmSaveBtn">
                <span id="llmSaveTxt">💾 Save LLM Settings</span>
                <span id="llmSaveSpinner" class="spinner-border spinner-border-sm ms-1 d-none"></span>
            </button>
        </div>
    </div>
</form>
</div><!-- /tab-pane-llm -->
```

Replace with:
```html
<div id="tab-pane-llm" class="settings-tab-pane pt-4">
<form method="post" id="llmForm" novalidate>
    @Html.AntiForgeryToken()
    @* Hidden field — JS writes the selected model ID here before save *@
    <input type="hidden" asp-for="Input.ModelId" id="llmModelIdHidden" />

    <div class="card shadow-sm mb-4">
        <div class="card-header bg-white py-3">
            <div class="settings-section-header">🤖 OpenRouter Configuration</div>
        </div>
        <div class="card-body">

            @* ── API Key ───────────────────────────────────────────────────── *@
            <div class="mb-4">
                <label class="form-label fw-semibold">API Key</label>
                <div class="input-group">
                    <input type="password" id="orApiKey"
                           name="Input.ApiKeys[0]"
                           value="@(Model.Input.ApiKeys.Count > 0 ? Model.Input.ApiKeys[0] : "")"
                           class="form-control" placeholder="sk-or-…"
                           spellcheck="false" autocomplete="off" />
                    <button type="button" class="btn btn-outline-secondary" id="orKeyToggle" title="Show/hide">👁</button>
                </div>
                <div id="orKeyStatus" class="form-text mt-1"></div>
                <div class="form-text">
                    Your <a href="https://openrouter.ai/keys" target="_blank" rel="noopener">OpenRouter API key</a>.
                    <strong>Global setting:</strong> applies to every tenant.
                </div>
            </div>

            @* ── Model Picker (shown after models load) ─────────────────── *@
            <div id="orModelSection" class="@(Model.Input.ApiKeys.Count > 0 && !string.IsNullOrWhiteSpace(Model.Input.ApiKeys[0]) ? "" : "d-none")">
                <hr class="my-3" />
                <label class="form-label fw-semibold">Model</label>

                @* Filter bar *@
                <div class="row g-2 mb-2">
                    <div class="col-md-5">
                        <input type="text" id="orModelSearch" class="form-control form-control-sm"
                               placeholder="Search models…" autocomplete="off" />
                    </div>
                    <div class="col-md-4">
                        <select id="orProviderFilter" class="form-select form-select-sm">
                            <option value="">All providers</option>
                        </select>
                    </div>
                    <div class="col-md-3 d-flex align-items-center">
                        <div class="form-check mb-0">
                            <input class="form-check-input" type="checkbox" id="orFreeOnly" />
                            <label class="form-check-label small" for="orFreeOnly">Free only</label>
                        </div>
                    </div>
                </div>

                @* Model list *@
                <select id="orModelList" class="form-select" size="8" style="font-family:monospace;font-size:.82rem;">
                </select>
                <div class="form-text mt-1" id="orModelCount"></div>
            </div>

        </div>
        <div class="card-footer bg-white border-top d-flex justify-content-end py-3">
            <button type="submit" class="btn btn-primary px-4" id="llmSaveBtn">
                <span id="llmSaveTxt">💾 Save LLM Settings</span>
                <span id="llmSaveSpinner" class="spinner-border spinner-border-sm ms-1 d-none"></span>
            </button>
        </div>
    </div>
</form>
</div><!-- /tab-pane-llm -->
```

- [ ] **Step 2: Update the agent modal Model Override field**

Find:
```html
                            <input asp-for="AgentDef.ModelId" id="agentModelId" class="form-control"
                                   list="agentGeminiModels" placeholder="Leave blank to use global model"
                                   spellcheck="false" autocomplete="off" />
                            <datalist id="agentGeminiModels">
                                <option value="gemini-2.0-flash">gemini-2.0-flash</option>
                                <option value="gemini-2.5-pro-preview-05-06">gemini-2.5-pro-preview</option>
                                <option value="gemini-1.5-pro">gemini-1.5-pro</option>
                                <option value="gemini-1.5-flash">gemini-1.5-flash</option>
                                <option value="gemini-2.0-flash-lite">gemini-2.0-flash-lite</option>
                            </datalist>
```
Replace with:
```html
                            <select id="agentModelId" name="AgentDef.ModelId" class="form-select"
                                    style="font-family:monospace;font-size:.82rem;">
                                <option value="">(use global model)</option>
                            </select>
```

- [ ] **Step 3: Remove the LLM JS block and replace with OpenRouter JS**

Find the JS block delimited by:
```js
    // ── LLM: API key list (SuperAdmin only) ──────────────────────────────────
```
...through...
```js
    } // end LLM-only block
```

Replace the entire block with:
```js
    // ── LLM / OpenRouter ────────────────────────────────────────────────────
    @if (isSuperAdmin)
    {
    <text>
    (function () {
        let _allModels = [];

        // ── Key show/hide ────────────────────────────────────────────────────
        document.getElementById('orKeyToggle').addEventListener('click', function () {
            var f = document.getElementById('orApiKey');
            f.type = f.type === 'password' ? 'text' : 'password';
        });

        // ── Fetch models with debounce ───────────────────────────────────────
        var _debounceTimer = null;
        document.getElementById('orApiKey').addEventListener('input', function () {
            clearTimeout(_debounceTimer);
            _debounceTimer = setTimeout(function () {
                fetchModels(document.getElementById('orApiKey').value.trim());
            }, 500);
        });

        function setStatus(msg, type) {
            // type: 'loading' | 'success' | 'error'
            var el = document.getElementById('orKeyStatus');
            var colors = { loading: 'text-muted', success: 'text-success', error: 'text-danger' };
            el.className = 'form-text mt-1 ' + (colors[type] || 'text-muted');
            el.textContent = msg;
        }

        function fetchModels(apiKey) {
            if (!apiKey) {
                document.getElementById('orModelSection').classList.add('d-none');
                setStatus('', '');
                return;
            }
            setStatus('⏳ Loading models…', 'loading');
            fetch('/Settings?handler=OpenRouterModels&apiKey=' + encodeURIComponent(apiKey))
                .then(function (r) { return r.json().then(function (d) { return { ok: r.ok, data: d }; }); })
                .then(function (res) {
                    if (!res.ok || res.data.error) {
                        setStatus('❌ ' + (res.data.error || 'Unknown error'), 'error');
                        document.getElementById('orModelSection').classList.add('d-none');
                        return;
                    }
                    _allModels = res.data.models;
                    setStatus('✓ ' + _allModels.length + ' models loaded', 'success');
                    populateProviderFilter();
                    applyFilters();
                    populateAgentModelPicker();
                    document.getElementById('orModelSection').classList.remove('d-none');
                })
                .catch(function (err) {
                    setStatus('❌ Could not reach OpenRouter', 'error');
                    document.getElementById('orModelSection').classList.add('d-none');
                });
        }

        function populateProviderFilter() {
            var sel = document.getElementById('orProviderFilter');
            var current = sel.value;
            sel.innerHTML = '<option value="">All providers</option>';
            var providers = [...new Set(_allModels.map(function (m) { return m.provider; }))].sort();
            providers.forEach(function (p) {
                var o = document.createElement('option');
                o.value = p; o.textContent = p;
                if (p === current) o.selected = true;
                sel.appendChild(o);
            });
        }

        function formatCost(m) {
            if (m.isFree) return 'FREE';
            var fmt = function (n) {
                return n === 0 ? '$0' : '$' + n.toFixed(n < 0.01 ? 4 : 2);
            };
            return fmt(m.inputCost) + ' / 1M in · ' + fmt(m.outputCost) + ' / 1M out';
        }

        function applyFilters() {
            var search   = document.getElementById('orModelSearch').value.toLowerCase();
            var provider = document.getElementById('orProviderFilter').value;
            var freeOnly = document.getElementById('orFreeOnly').checked;
            var current  = document.getElementById('llmModelIdHidden').value;

            var filtered = _allModels.filter(function (m) {
                if (freeOnly && !m.isFree) return false;
                if (provider && m.provider !== provider) return false;
                if (search && !m.name.toLowerCase().includes(search) &&
                    !m.id.toLowerCase().includes(search)) return false;
                return true;
            });

            var list = document.getElementById('orModelList');
            list.innerHTML = '';
            filtered.forEach(function (m) {
                var o = document.createElement('option');
                o.value = m.id;
                var cost = formatCost(m);
                o.textContent = m.name + ' (' + m.id + ')  —  ' + cost;
                if (m.id === current) o.selected = true;
                list.appendChild(o);
            });

            document.getElementById('orModelCount').textContent =
                filtered.length + ' of ' + _allModels.length + ' models shown';
        }

        // Write selection into the hidden field for form submission
        document.getElementById('orModelList').addEventListener('change', function () {
            document.getElementById('llmModelIdHidden').value = this.value;
        });

        // Filters fire client-side
        ['orModelSearch', 'orProviderFilter', 'orFreeOnly'].forEach(function (id) {
            document.getElementById(id).addEventListener(
                id === 'orFreeOnly' ? 'change' : 'input', applyFilters);
        });

        // ── Agent modal model picker ─────────────────────────────────────────
        function populateAgentModelPicker() {
            var sel = document.getElementById('agentModelId');
            var current = sel ? sel.value : '';
            if (!sel) return;
            sel.innerHTML = '<option value="">(use global model)</option>';
            _allModels.forEach(function (m) {
                var o = document.createElement('option');
                o.value = m.id;
                o.textContent = m.name + ' (' + m.id + ')';
                if (m.id === current) o.selected = true;
                sel.appendChild(o);
            });
        }

        // Re-populate agent picker when modal opens (restores saved ModelId)
        document.addEventListener('show.bs.modal', function (e) {
            if (e.target && e.target.id === 'agentModal') {
                if (_allModels.length > 0) populateAgentModelPicker();
            }
        });

        // Auto-fetch on page load if a key is already saved
        var savedKey = document.getElementById('orApiKey').value.trim();
        if (savedKey) fetchModels(savedKey);

    })();
    </text>
    }
    // ── end LLM-only block ───────────────────────────────────────────────────
```

- [ ] **Step 4: Remove any remaining `ApiVersion` or `agentGeminiModels` references in the JS**

Search `Settings.cshtml` for `agentGeminiModels` and `ApiVersion` — remove any remaining references. Also search for `agentModelId` usages that set `.value` to a Gemini model ID; they should just set `.value = agent.ModelId ?? ''` which is already correct.

In the agent modal JS (around line 3387 and 3413), find:
```js
            document.getElementById('agentModelId').value      = '';
```
and:
```js
            document.getElementById('agentModelId').value      = agent.ModelId ?? '';
```
These lines are already correct (they set the hidden-field-backed select value). No change needed.

- [ ] **Step 5: Build and verify**
```
dotnet build Pulse/Pulse.csproj
```
Expected: 0 errors.

- [ ] **Step 6: Commit**
```
git add Pulse/Pages/Settings.cshtml
git commit -m "feat: replace Gemini LLM tab with OpenRouter model picker UI"
```

---

## Self-Review Checklist

- [ ] All `GeminiKeyRotationService` references removed (grep for `GeminiKeyRotationService` returns 0 hits)
- [ ] All `ApiVersion` references removed from entities, models, services (grep for `ApiVersion` returns 0 hits except migrations)
- [ ] All `AddGoogleAIGeminiChatCompletion` references replaced (grep returns 0 hits)
- [ ] All `GeminiPromptExecutionSettings` references replaced (grep returns 0 hits)
- [ ] `OpenRouterService` is registered in DI and injected into 3 agent runners
- [ ] `OnGetOpenRouterModelsAsync` is SuperAdmin-guarded
- [ ] JS model picker writes to `#llmModelIdHidden` before form post
- [ ] Agent modal `#agentModelId` is populated from `_allModels` on modal open
- [ ] Build passes: `dotnet build Pulse/Pulse.csproj` → 0 errors
