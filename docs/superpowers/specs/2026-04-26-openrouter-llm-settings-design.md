# OpenRouter LLM Settings — Design Spec
**Date:** 2026-04-26  
**Branch:** feature/multi-tenancy  
**Scope:** Replace Gemini with OpenRouter as the sole LLM provider in the SuperAdmin LLM settings tab, with a live model picker, cost display, and filtering.

---

## Overview

The LLM settings tab currently supports only Google Gemini (free-text Model ID + API version dropdown + multi-key rotation). This change removes Gemini entirely and replaces it with OpenRouter, which acts as a unified gateway to many providers. The key UX improvement is a live model dropdown populated from the OpenRouter API — with per-model costs and filtering — rather than a free-text field.

---

## Data Model Changes

### `GlobalLlmSettings` entity
- **Remove** `ApiVersion` column (Gemini-specific, no equivalent for OpenRouter).
- `ModelId` stays — stores the selected OpenRouter model ID (e.g. `openai/gpt-4o`).
- No `Provider` field needed; OpenRouter is the only provider.
- EF migration required to drop the `ApiVersion` column.

### `AppApiKeyEntity`
- Structure unchanged. OpenRouter needs only one API key per installation.
- Multi-key storage is kept but round-robin rotation is no longer meaningful; the first active key is used.

### Removed
- `GeminiKeyRotationService` — deleted entirely.
- All Gemini-specific references in `GlobalLlmSettingsService`, `LlmSettingsService`, and the settings page.

---

## Backend — New Endpoint

**Handler:** `OnGetOpenRouterModelsAsync(string apiKey)` on `Settings.cshtml.cs`

- Access: SuperAdmin only (same auth guard as the rest of the LLM tab).
- Calls `GET https://openrouter.ai/api/v1/models` with `Authorization: Bearer {apiKey}`.
- Maps each model to a trimmed DTO:
  ```csharp
  record OpenRouterModelDto(
      string Id,
      string Name,
      string Provider,   // prefix before first '/' in Id, e.g. "openai"
      decimal InputCost, // per-token prompt cost, scaled to per-million for display
      decimal OutputCost,
      bool IsFree        // true when both InputCost == 0 and OutputCost == 0
  );
  ```
- Returns `JsonResult` with `{ models: OpenRouterModelDto[] }`.
- Cached in `IMemoryCache` for 1 hour, keyed by `"OpenRouterModels_" + SHA256(apiKey)[..16]`.
- On fetch failure (bad key, network error) returns a 400/502 with `{ error: "..." }`.

---

## Frontend — LLM Tab

### Removed
- Model ID free-text input and its help text.
- Gemini API Version dropdown.
- All Gemini-branded labels, placeholders, and datalists.

### API Key Field
- Single password input (replaces the multi-key list).
- Show/hide toggle (👁 button).
- 500ms debounce after input change triggers model fetch.
- Inline status indicator below the field:
  - Spinner while loading.
  - Green `✓ N models loaded` on success.
  - Red error message on failure (e.g. "Invalid API key" / "Could not reach OpenRouter").

### Model Picker (visible after models load)
- **Filter bar** — three controls in a row:
  1. Text search input — filters model `name` and `id` (case-insensitive contains, client-side).
  2. "Free only" checkbox — hides models where `isFree === false`.
  3. Provider `<select>` — options populated from distinct `provider` values in the fetched list (e.g. openai, anthropic, google, meta, mistral). Default: "All providers".
- **Model list** — a `<select size="8">` (scrollable) or styled list:
  - Each option shows: `{Name} ({Id})` with cost appended right-aligned.
  - Cost format: `$2.50 / 1M in · $10.00 / 1M out`
  - When both costs are $0: a `FREE` badge instead of cost figures.
- Selected model ID is written into a hidden `<input name="Input.ModelId">` on change.
- All three filters compose simultaneously (AND logic), applied client-side on the full fetched list.

### Page Load Behaviour
- If a saved API key exists when the page loads, JS immediately fires the model fetch (no user action needed).
- After models load, the saved `ModelId` is pre-selected in the list if present.

### Save
- Form posts `Input.ModelId` (hidden) + `Input.ApiKeys[0]` (the key field).
- `OnPostAsync` saves `ModelId` to `GlobalLlmSettings` and the key to `AppApiKeyEntity`.
- `ApiVersion` field is no longer posted or saved.

---

## Agent Definition Modal

- The existing `agentModelId` text input + `agentGeminiModels` datalist is replaced with the same model picker component.
- The picker is populated from the same JS model array already loaded for the LLM tab (no second fetch).
- If no models are loaded (no API key saved), the agent modal shows a plain text input as a fallback.

---

## Removed Service: `GeminiKeyRotationService`

Deleted. Callers that previously used `GeminiKeyRotationService.GetNextKey(keys)` switch to reading `AppApiKeyEntity` directly for the first active key. If no valid key exists, throw a clear `InvalidOperationException("No OpenRouter API key configured.")`.

---

## Error States

| Situation | Behaviour |
|---|---|
| Empty API key field | Model picker hidden, no fetch |
| Bad API key (401 from OpenRouter) | Red status "Invalid API key" |
| Network failure | Red status "Could not reach OpenRouter" |
| Key valid but no models returned | Red status "No models found for this key" |
| Saved ModelId not in fetched list | Selection falls back to first model in list, user must re-save |

---

## Out of Scope

- Per-tenant API keys (keys remain global/SuperAdmin-managed).
- Multiple provider support (OpenRouter only).
- Persisting the model list to the database.
- OpenRouter streaming or tool-use configuration (handled at call site, not settings).
