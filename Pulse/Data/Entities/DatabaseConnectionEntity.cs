using Pulse.Models;

namespace Pulse.Data.Entities;

public class DatabaseConnectionEntity : ITenantOwned
{
    public Guid TenantId { get; set; }
    /// <summary>Logical connection ID (formerly the dictionary key).</summary>
    public string           Id               { get; set; } = "";
    public string           Label            { get; set; } = "";
    public DatabaseProvider Provider         { get; set; } = DatabaseProvider.SqlServer;
    public string           ConnectionString { get; set; } = "";
    public bool             IsEnabled        { get; set; } = true;
    public bool             ReadOnly         { get; set; } = true;
    public int              MaxRows          { get; set; } = 500;

    /// <summary>JSON-serialised <c>List&lt;string&gt;</c>.</summary>
    public string           AllowedSchemasJson { get; set; } = "[]";
}
