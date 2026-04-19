using System.Text.Json;

namespace Pulse.Services;

/// <summary>
/// MCP-style database tools exposed as a C# service.
/// All methods return <see cref="JsonElement"/> so the results are trivially
/// serialisable for LLM consumption or HTTP responses.
/// </summary>
public interface IDatabaseTools
{
    /// <summary>Returns all configured connection IDs and their labels/providers.</summary>
    JsonElement ListConnections();

    /// <summary>
    /// Returns metadata (schema, name, type) for every table and view visible
    /// to the database user associated with <paramref name="connectionId"/>.
    /// </summary>
    Task<JsonElement> ListTablesAsync(string connectionId, CancellationToken ct = default);

    /// <summary>
    /// Returns column definitions (name, data type, nullability, max length,
    /// default value) for <paramref name="tableName"/> in the given connection.
    /// </summary>
    Task<JsonElement> GetTableSchemaAsync(
        string connectionId, string tableName, CancellationToken ct = default);

    /// <summary>
    /// Executes a parameterized SQL SELECT query and returns all result rows as a JSON array.
    /// Results are capped at the per-connection MaxRows limit.
    /// </summary>
    Task<JsonElement> ExecuteQueryAsync(
        string connectionId,
        string query,
        JsonElement? parameters = null,
        CancellationToken ct = default);

    // ── Write operations (require ReadOnly = false on the connection) ─────────

    /// <summary>
    /// Executes any DDL or DML statement (CREATE, ALTER, DROP, INSERT, UPDATE, DELETE, TRUNCATE, …)
    /// on a read-write connection and returns the number of rows affected.
    /// </summary>
    Task<JsonElement> ExecuteNonQueryAsync(
        string connectionId,
        string sql,
        JsonElement? parameters = null,
        CancellationToken ct = default);

    /// <summary>
    /// Creates a new table with the given <paramref name="columnDefinitions"/> SQL fragment
    /// (the text that goes inside the parentheses of CREATE TABLE).
    /// </summary>
    Task<JsonElement> CreateTableAsync(
        string connectionId,
        string tableName,
        string columnDefinitions,
        bool ifNotExists = true,
        CancellationToken ct = default);

    /// <summary>Drops a table, optionally skipping the error when it does not exist.</summary>
    Task<JsonElement> DropTableAsync(
        string connectionId,
        string tableName,
        bool ifExists = true,
        CancellationToken ct = default);

    /// <summary>Removes all rows from a table without deleting its structure.</summary>
    Task<JsonElement> TruncateTableAsync(
        string connectionId,
        string tableName,
        CancellationToken ct = default);

    /// <summary>Adds a new column to an existing table.</summary>
    Task<JsonElement> AddColumnAsync(
        string connectionId,
        string tableName,
        string columnName,
        string columnDefinition,
        CancellationToken ct = default);

    /// <summary>Removes a column from an existing table.</summary>
    Task<JsonElement> DropColumnAsync(
        string connectionId,
        string tableName,
        string columnName,
        CancellationToken ct = default);

    /// <summary>Renames an existing table using the provider-appropriate syntax.</summary>
    Task<JsonElement> RenameTableAsync(
        string connectionId,
        string oldTableName,
        string newTableName,
        CancellationToken ct = default);

    /// <summary>
    /// Opens the connection, measures latency, then runs a provider-specific probe to report
    /// the current DB user, server version, whether the database is a read-only replica, and
    /// whether the DB user has INSERT / UPDATE / DELETE / CREATE TABLE permissions.
    /// Returns an <c>effectiveAccess</c> field: one of
    /// <c>read-write</c>, <c>read-only (config)</c>, <c>read-only (database)</c>,
    /// or <c>read-only (permissions)</c>.
    /// </summary>
    Task<JsonElement> TestConnectionAsync(
        string connectionId,
        CancellationToken ct = default);
}
