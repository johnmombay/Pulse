namespace Pulse.Models;

/// <summary>
/// Root IOptions type bound from the <c>DatabaseConnections</c> section in appsettings.json.
/// Key = logical connection ID used by the agent (e.g. <c>"sales-db"</c>, <c>"analytics"</c>).
/// </summary>
public sealed class DatabaseConnectionsOptions
{
    /// <summary>Named database connections keyed by a logical identifier.</summary>
    public Dictionary<string, DatabaseConnectionEntry> Connections { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}
