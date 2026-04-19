namespace Pulse.Data.Entities;

/// <summary>One Gemini API key. Order is preserved via <see cref="SortOrder"/>.</summary>
public class AppApiKeyEntity
{
    public int    Id        { get; set; }
    public string Key       { get; set; } = "";
    public int    SortOrder { get; set; }
}
