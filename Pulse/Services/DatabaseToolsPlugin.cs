using System.ComponentModel;
using System.Data.Common;
using System.Text.Json;
using Microsoft.SemanticKernel;

namespace Pulse.Services;

/// <summary>
/// Exposes <see cref="IDatabaseTools"/> as Semantic Kernel
/// <see cref="KernelFunction"/>s so the agent can invoke them exactly like
/// MCP tools — with automatic argument parsing and structured JSON responses.
///
/// Registration (Program.cs):
/// <code>
///   builder.Services.AddTransient&lt;DatabaseToolsPlugin&gt;();
/// </code>
///
/// Wiring into the kernel (AgentOrchestrationService):
/// <code>
///   kernel.Plugins.AddFromObject(dbToolsPlugin, "Database");
/// </code>
/// </summary>
public sealed class DatabaseToolsPlugin(
    IDatabaseTools tools,
    ILogger<DatabaseToolsPlugin> logger)
{
    // ── Tool: list_connections ────────────────────────────────────────────────

    [KernelFunction("db_list_connections")]
    [Description(
        "Lists all configured database connection IDs, their labels, providers " +
        "(SqlServer, AzureSQL, PostgreSQL, MySQL, MariaDB, Oracle), and whether they are " +
        "configured as read-only. Call this first to discover available connectionId values. " +
        "Use db_test_connection to verify the actual effective access level.")]
    public string ListConnections()
    {
        logger.LogInformation("Agent called db_list_connections");
        return tools.ListConnections().GetRawText();
    }

    // ── Tool: db_test_connection ──────────────────────────────────────────────

    [KernelFunction("db_test_connection")]
    [Description(
        "Opens a connection and reports: latency, server name, server version, current DB user, " +
        "and — most importantly — the 'effectiveAccess' field which is one of: " +
        "'read-write', 'read-only (config)', 'read-only (database)', 'read-only (permissions)'. " +
        "'read-only (config)' means the app setting prevents writes but the DB user could write. " +
        "'read-only (database)' means the DB itself is a replica or snapshot. " +
        "'read-only (permissions)' means the DB user lacks INSERT/UPDATE/DELETE grants. " +
        "Call this before attempting DDL or DML to confirm whether the connection actually allows writes.")]
    public async Task<string> TestConnectionAsync(
        [Description("Connection ID from DatabaseConnections configuration.")]
        string connectionId,
        CancellationToken ct = default)
    {
        logger.LogInformation("Agent called db_test_connection({Id})", connectionId);
        try
        {
            var result = await tools.TestConnectionAsync(connectionId, ct);
            return result.GetRawText();
        }
        catch (Exception ex) when (ex is KeyNotFoundException or InvalidOperationException or DbException)
        {
            logger.LogWarning(ex, "db_test_connection failed: {Message}", ex.Message);
            return JsonSerializer.Serialize(new { error = ex.Message });
        }
    }

    // ── Tool: db_list_tables ──────────────────────────────────────────────────

    [KernelFunction("db_list_tables")]
    [Description(
        "Returns a JSON array of all tables and views accessible in the specified database " +
        "connection. Each entry contains tableSchema, tableName, and tableType.")]
    public async Task<string> ListTablesAsync(
        [Description("Connection ID from DatabaseConnections configuration, e.g. 'sales-db'.")]
        string connectionId,
        CancellationToken ct = default)
    {
        logger.LogInformation("Agent called db_list_tables({Id})", connectionId);
        try
        {
            var result = await tools.ListTablesAsync(connectionId, ct);
            return result.GetRawText();
        }
        catch (Exception ex) when (ex is KeyNotFoundException or InvalidOperationException or DbException)
        {
            logger.LogWarning(ex, "db_list_tables failed: {Message}", ex.Message);
            return JsonSerializer.Serialize(new { error = ex.Message });
        }
    }

    // ── Tool: db_get_schema ───────────────────────────────────────────────────

    [KernelFunction("db_get_schema")]
    [Description(
        "Returns column definitions for a specific table: column name, data type, " +
        "nullability, maximum length, and default value.")]
    public async Task<string> GetTableSchemaAsync(
        [Description("Connection ID from DatabaseConnections configuration.")]
        string connectionId,
        [Description("Exact table name as it appears in the database (case-sensitive on PostgreSQL/Oracle).")]
        string tableName,
        CancellationToken ct = default)
    {
        logger.LogInformation("Agent called db_get_schema({Id}, {Table})", connectionId, tableName);
        try
        {
            var result = await tools.GetTableSchemaAsync(connectionId, tableName, ct);
            return result.GetRawText();
        }
        catch (Exception ex) when (ex is KeyNotFoundException or InvalidOperationException or DbException)
        {
            logger.LogWarning(ex, "db_get_schema failed: {Message}", ex.Message);
            return JsonSerializer.Serialize(new { error = ex.Message });
        }
    }

    // ── Tool: db_execute_query ────────────────────────────────────────────────

    [KernelFunction("db_execute_query")]
    [Description(
        "Executes a parameterized SQL query and returns the result rows as a JSON array. " +
        "Always use @paramName placeholders (or :paramName for Oracle) to avoid SQL injection. " +
        "Example query: 'SELECT * FROM Orders WHERE CustomerId = @customerId AND Status = @status'. " +
        "Example parameters: '{\"customerId\": 42, \"status\": \"active\"}'. " +
        "Results are capped at the per-connection MaxRows limit (default 500).")]
    public async Task<string> ExecuteQueryAsync(
        [Description("Connection ID from DatabaseConnections configuration.")]
        string connectionId,
        [Description("Parameterized SQL query using @param placeholders (or :param for Oracle).")]
        string query,
        [Description(
            "Optional JSON object mapping parameter names to values, " +
            "e.g. {\"customerId\": 42, \"status\": \"active\"}. " +
            "Omit or pass null if the query has no parameters.")]
        string? parametersJson = null,
        CancellationToken ct = default)
    {
        logger.LogInformation(
            "Agent called db_execute_query({Id}): {Query}", connectionId,
            query.Length > 100 ? string.Concat(query.AsSpan(0, 100), "…") : query);

        JsonElement? parameters = null;
        if (!string.IsNullOrWhiteSpace(parametersJson))
        {
            try { parameters = JsonDocument.Parse(parametersJson).RootElement; }
            catch (JsonException ex)
            {
                return JsonSerializer.Serialize(new { error = $"Invalid parametersJson: {ex.Message}" });
            }
        }

        try
        {
            var result = await tools.ExecuteQueryAsync(connectionId, query, parameters, ct);
            return result.GetRawText();
        }
        catch (Exception ex) when (ex is KeyNotFoundException or InvalidOperationException or ArgumentException or DbException)
        {
            logger.LogWarning(ex, "db_execute_query rejected: {Message}", ex.Message);
            return JsonSerializer.Serialize(new { error = ex.Message });
        }
    }

    // ── Tool: db_execute_nonquery

    [KernelFunction("db_execute_nonquery")]
    [Description(
        "Executes any DDL or DML statement on a read-write connection and returns the number of " +
        "rows affected. Use this for INSERT, UPDATE, DELETE, CREATE INDEX, ALTER TABLE, etc. " +
        "For creating or dropping whole tables prefer the dedicated tools. " +
        "The connection must have ReadOnly = false.")]
    public async Task<string> ExecuteNonQueryAsync(
        [Description("Connection ID from DatabaseConnections configuration.")]
        string connectionId,
        [Description("DDL or DML SQL statement, e.g. \"UPDATE Orders SET Status='shipped' WHERE Id=@id\".")]
        string sql,
        [Description("Optional JSON object of parameter values, e.g. {\"id\": 42}. Omit if not needed.")]
        string? parametersJson = null,
        CancellationToken ct = default)
    {
        logger.LogInformation("Agent called db_execute_nonquery({Id})", connectionId);

        JsonElement? parameters = null;
        if (!string.IsNullOrWhiteSpace(parametersJson))
        {
            try { parameters = JsonDocument.Parse(parametersJson).RootElement; }
            catch (JsonException ex)
            {
                return JsonSerializer.Serialize(new { error = $"Invalid parametersJson: {ex.Message}" });
            }
        }

        try
        {
            var result = await tools.ExecuteNonQueryAsync(connectionId, sql, parameters, ct);
            return result.GetRawText();
        }
        catch (Exception ex) when (ex is KeyNotFoundException or InvalidOperationException or ArgumentException or DbException)
        {
            logger.LogWarning(ex, "db_execute_nonquery rejected: {Message}", ex.Message);
            return JsonSerializer.Serialize(new { error = ex.Message });
        }
    }

    // ── Tool: db_create_table ─────────────────────────────────────────────────

    [KernelFunction("db_create_table")]
    [Description(
        "Creates a new table. Provide the column definitions exactly as they would appear inside " +
        "the parentheses of a CREATE TABLE statement for the target database, " +
        "e.g. \"id INT PRIMARY KEY, name NVARCHAR(100) NOT NULL, created_at DATETIME DEFAULT GETDATE()\". " +
        "The connection must have ReadOnly = false.")]
    public async Task<string> CreateTableAsync(
        [Description("Connection ID from DatabaseConnections configuration.")]
        string connectionId,
        [Description("Name of the table to create.")]
        string tableName,
        [Description("SQL column definitions (the contents of the parentheses), provider-appropriate syntax.")]
        string columnDefinitions,
        [Description("Skip creation without error if the table already exists. Defaults to true.")]
        bool ifNotExists = true,
        CancellationToken ct = default)
    {
        logger.LogInformation("Agent called db_create_table({Id}, {Table})", connectionId, tableName);
        try
        {
            var result = await tools.CreateTableAsync(connectionId, tableName, columnDefinitions, ifNotExists, ct);
            return result.GetRawText();
        }
        catch (Exception ex) when (ex is KeyNotFoundException or InvalidOperationException or ArgumentException or DbException)
        {
            logger.LogWarning(ex, "db_create_table rejected: {Message}", ex.Message);
            return JsonSerializer.Serialize(new { error = ex.Message });
        }
    }

    // ── Tool: db_drop_table ───────────────────────────────────────────────────

    [KernelFunction("db_drop_table")]
    [Description(
        "Drops (permanently deletes) a table and all of its data. This action is irreversible. " +
        "The connection must have ReadOnly = false.")]
    public async Task<string> DropTableAsync(
        [Description("Connection ID from DatabaseConnections configuration.")]
        string connectionId,
        [Description("Name of the table to drop.")]
        string tableName,
        [Description("Skip the error when the table does not exist. Defaults to true.")]
        bool ifExists = true,
        CancellationToken ct = default)
    {
        logger.LogInformation("Agent called db_drop_table({Id}, {Table})", connectionId, tableName);
        try
        {
            var result = await tools.DropTableAsync(connectionId, tableName, ifExists, ct);
            return result.GetRawText();
        }
        catch (Exception ex) when (ex is KeyNotFoundException or InvalidOperationException or DbException)
        {
            logger.LogWarning(ex, "db_drop_table rejected: {Message}", ex.Message);
            return JsonSerializer.Serialize(new { error = ex.Message });
        }
    }

    // ── Tool: db_truncate_table ───────────────────────────────────────────────

    [KernelFunction("db_truncate_table")]
    [Description(
        "Removes all rows from a table without dropping the table structure. " +
        "Faster than DELETE with no WHERE clause. This action is irreversible. " +
        "The connection must have ReadOnly = false.")]
    public async Task<string> TruncateTableAsync(
        [Description("Connection ID from DatabaseConnections configuration.")]
        string connectionId,
        [Description("Name of the table to truncate.")]
        string tableName,
        CancellationToken ct = default)
    {
        logger.LogInformation("Agent called db_truncate_table({Id}, {Table})", connectionId, tableName);
        try
        {
            var result = await tools.TruncateTableAsync(connectionId, tableName, ct);
            return result.GetRawText();
        }
        catch (Exception ex) when (ex is KeyNotFoundException or InvalidOperationException or DbException)
        {
            logger.LogWarning(ex, "db_truncate_table rejected: {Message}", ex.Message);
            return JsonSerializer.Serialize(new { error = ex.Message });
        }
    }

    // ── Tool: db_add_column ───────────────────────────────────────────────────

    [KernelFunction("db_add_column")]
    [Description(
        "Adds a new column to an existing table. Provide the column definition in the syntax " +
        "appropriate for the target database, e.g. \"NVARCHAR(200) NOT NULL DEFAULT ''\". " +
        "The connection must have ReadOnly = false.")]
    public async Task<string> AddColumnAsync(
        [Description("Connection ID from DatabaseConnections configuration.")]
        string connectionId,
        [Description("Name of the table to alter.")]
        string tableName,
        [Description("Name of the new column.")]
        string columnName,
        [Description("Column type and constraints, e.g. \"INT NOT NULL DEFAULT 0\".")]
        string columnDefinition,
        CancellationToken ct = default)
    {
        logger.LogInformation(
            "Agent called db_add_column({Id}, {Table}, {Col})", connectionId, tableName, columnName);
        try
        {
            var result = await tools.AddColumnAsync(connectionId, tableName, columnName, columnDefinition, ct);
            return result.GetRawText();
        }
        catch (Exception ex) when (ex is KeyNotFoundException or InvalidOperationException or DbException)
        {
            logger.LogWarning(ex, "db_add_column rejected: {Message}", ex.Message);
            return JsonSerializer.Serialize(new { error = ex.Message });
        }
    }

    // ── Tool: db_drop_column ──────────────────────────────────────────────────

    [KernelFunction("db_drop_column")]
    [Description(
        "Removes a column from an existing table. This action is irreversible and deletes all data " +
        "in that column. The connection must have ReadOnly = false.")]
    public async Task<string> DropColumnAsync(
        [Description("Connection ID from DatabaseConnections configuration.")]
        string connectionId,
        [Description("Name of the table to alter.")]
        string tableName,
        [Description("Name of the column to drop.")]
        string columnName,
        CancellationToken ct = default)
    {
        logger.LogInformation(
            "Agent called db_drop_column({Id}, {Table}, {Col})", connectionId, tableName, columnName);
        try
        {
            var result = await tools.DropColumnAsync(connectionId, tableName, columnName, ct);
            return result.GetRawText();
        }
        catch (Exception ex) when (ex is KeyNotFoundException or InvalidOperationException or DbException)
        {
            logger.LogWarning(ex, "db_drop_column rejected: {Message}", ex.Message);
            return JsonSerializer.Serialize(new { error = ex.Message });
        }
    }

    // ── Tool: db_rename_table ─────────────────────────────────────────────────

    [KernelFunction("db_rename_table")]
    [Description(
        "Renames an existing table using the provider-appropriate syntax " +
        "(sp_rename for SQL Server, RENAME TABLE for MySQL, ALTER TABLE … RENAME TO for others). " +
        "The connection must have ReadOnly = false.")]
    public async Task<string> RenameTableAsync(
        [Description("Connection ID from DatabaseConnections configuration.")]
        string connectionId,
        [Description("Current name of the table.")]
        string oldTableName,
        [Description("New name for the table.")]
        string newTableName,
        CancellationToken ct = default)
    {
        logger.LogInformation(
            "Agent called db_rename_table({Id}, {Old} → {New})", connectionId, oldTableName, newTableName);
        try
        {
            var result = await tools.RenameTableAsync(connectionId, oldTableName, newTableName, ct);
            return result.GetRawText();
        }
        catch (Exception ex) when (ex is KeyNotFoundException or InvalidOperationException or DbException)
        {
            logger.LogWarning(ex, "db_rename_table rejected: {Message}", ex.Message);
            return JsonSerializer.Serialize(new { error = ex.Message });
        }
    }
}
