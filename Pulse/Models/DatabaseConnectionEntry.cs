namespace Pulse.Models;

/// <summary>Supported database providers, including cloud-hosted variants.</summary>
public enum DatabaseProvider
{
    /// <summary>On-premises SQL Server or Azure SQL with SQL / Windows auth.</summary>
    SqlServer,

    /// <summary>
    /// Azure SQL with Azure AD / Managed Identity authentication.
    /// Uses the same <c>Microsoft.Data.SqlClient</c> driver as SqlServer but
    /// the connection string should include <c>Authentication=Active Directory Default</c>
    /// or <c>Authentication=Active Directory Managed Identity</c>.
    /// </summary>
    AzureSQL,

    /// <summary>PostgreSQL, AWS RDS for PostgreSQL, AWS Aurora PostgreSQL, Azure Database for PostgreSQL.</summary>
    PostgreSQL,

    /// <summary>MySQL, AWS RDS for MySQL, AWS Aurora MySQL, Azure Database for MySQL.</summary>
    MySQL,

    /// <summary>MariaDB.</summary>
    MariaDB,

    /// <summary>Oracle Database (on-premises or OCI cloud).</summary>
    Oracle,
}

/// <summary>
/// Maps a logical connection ID to a provider, connection string, and safety settings.
/// Bound from <c>DatabaseConnections:{id}</c> in appsettings.
/// </summary>
public sealed class DatabaseConnectionEntry
{
    /// <summary>Human-readable label shown in agent descriptions and logs.</summary>
    public string Label { get; set; } = "";

    /// <summary>Database engine to use when creating the ADO.NET connection.</summary>
    public DatabaseProvider Provider { get; set; } = DatabaseProvider.SqlServer;

    /// <summary>ADO.NET connection string. Store secrets in User Secrets or Azure Key Vault.</summary>
    public string ConnectionString { get; set; } = "";

    /// <summary>When <c>false</c> the connection is hidden from the agent and unavailable.</summary>
    public bool IsEnabled { get; set; } = true;

    /// <summary>
    /// When <c>true</c> the service rejects any query that does not start with SELECT,
    /// WITH, or EXEC. Defaults to <c>true</c>.
    /// </summary>
    public bool ReadOnly { get; set; } = true;

    /// <summary>Maximum rows returned by <c>ExecuteQueryAsync</c>. Default 500.</summary>
    public int MaxRows { get; set; } = 500;

    /// <summary>
    /// Optional schema whitelist. Leave empty to allow all schemas visible to the DB user.
    /// </summary>
    public List<string> AllowedSchemas { get; set; } = [];
}
