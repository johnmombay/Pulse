namespace Pulse.Data.Entities;

/// <summary>
/// Global LLM configuration managed exclusively by SuperAdmin. Every tenant uses the
/// <see cref="ModelId"/> and <see cref="ApiVersion"/> stored in this singleton row —
/// per-tenant <c>AppSettingsEntity.ModelId</c> / <c>ApiVersion</c> values are ignored.
/// Singleton row enforced by CHECK constraint (Id = 1).
/// </summary>
public class GlobalLlmSettings
{
    public int Id { get; set; } = 1;

    /// <summary>
    /// Gemini model identifier sent to Google as-is
    /// (e.g. <c>gemini-2.0-flash</c>, <c>gemini-2.5-flash</c>, <c>gemini-2.5-pro</c>).
    /// </summary>
    public string ModelId { get; set; } = string.Empty;

    /// <summary>
    /// Google Gemini API version: <c>V1Beta</c> (default) or <c>V1</c>.
    /// </summary>
    public string ApiVersion { get; set; } = "V1Beta";
}
