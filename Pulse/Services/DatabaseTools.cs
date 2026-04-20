using Pulse.Infrastructure;
using Pulse.Models;
using Dapper;
using Microsoft.Data.SqlClient;
using MySqlConnector;
using Npgsql;
using Oracle.ManagedDataAccess.Client;
using System.Data.Common;
using System.Diagnostics;
using System.Text.Json;

namespace Pulse.Services;

/// <summary>
/// ADO.NET + Dapper implementation of <see cref="IDatabaseTools"/>.
///
/// Supports SQL Server, Azure SQL, PostgreSQL, MySQL, MariaDB, and Oracle.
/// Cloud databases (AWS RDS, Azure SQL, Aurora) are reached through standard
/// connection strings — no special code paths are required.
///
/// Safety guarantees:
///   • All user-supplied values go through Dapper <c>DynamicParameters</c> — never concatenated.
///   • Per-connection <c>ReadOnly</c> flag rejects non-SELECT statements.
///   • Per-connection <c>MaxRows</c> caps result sets.
///   • Connections are opened and disposed within every method call (no shared state).
/// </summary>
public sealed class DatabaseTools(
    LlmSettingsService settingsService,
    ITenantContext tenantContext,
    ILogger<DatabaseTools> logger)
    : IDatabaseTools
{
    private static readonly JsonSerializerOptions SerializerOpts =
        new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    // ── Public API ────────────────────────────────────────────────────────────

    /// <inheritdoc/>
    public JsonElement ListConnections()
    {
        var summary = (settingsService.GetAsync(tenantContext.TenantId ?? Guid.Empty).GetAwaiter().GetResult().DatabaseConnections ?? [])
            .Select(kvp => new
            {
                id       = kvp.Key,
                label    = kvp.Value.Label,
                provider = kvp.Value.Provider.ToString(),
                readOnly = kvp.Value.ReadOnly,
                maxRows  = kvp.Value.MaxRows,
                enabled  = kvp.Value.IsEnabled,
            });
        return Serialize(summary);
    }

    /// <inheritdoc/>
    public async Task<JsonElement> ListTablesAsync(
        string connectionId, CancellationToken ct = default)
    {
        var (entry, conn) = OpenConnection(connectionId);
        await using var _ = conn;
        await conn.OpenAsync(ct);

        var sql  = ListTablesSql(entry.Provider);
        var rows = await conn.QueryAsync<dynamic>(new CommandDefinition(sql, cancellationToken: ct));

        logger.LogInformation("ListTables({Id}): {N} table(s) returned", connectionId, rows.AsList().Count);
        return Serialize(rows);
    }

    /// <inheritdoc/>
    public async Task<JsonElement> GetTableSchemaAsync(
        string connectionId, string tableName, CancellationToken ct = default)
    {
        var (entry, conn) = OpenConnection(connectionId);
        await using var _ = conn;
        await conn.OpenAsync(ct);

        var (sql, param) = SchemaSql(entry.Provider, tableName);
        var rows = await conn.QueryAsync<dynamic>(new CommandDefinition(sql, param, cancellationToken: ct));

        logger.LogInformation("GetTableSchema({Id}, {Table})", connectionId, tableName);
        return Serialize(rows);
    }

    /// <inheritdoc/>
    public async Task<JsonElement> ExecuteQueryAsync(
        string connectionId,
        string query,
        JsonElement? parameters = null,
        CancellationToken ct = default)
    {
        var (entry, conn) = OpenConnection(connectionId);
        await using var _ = conn;

        ValidateQuery(query, entry);

        // Inject provider-level row limit
        var limitedQuery = ApplyRowLimit(query, entry.Provider, entry.MaxRows);

        await conn.OpenAsync(ct);
        var dp   = BuildParameters(parameters, entry.Provider);
        var rows = await conn.QueryAsync<dynamic>(new CommandDefinition(limitedQuery, dp, cancellationToken: ct));

        var list = rows.AsList();
        logger.LogInformation(
            "ExecuteQuery({Id}): {N} row(s) — {Query}",
            connectionId, list.Count, TruncateForLog(query));

        return Serialize(list);
    }

    /// <inheritdoc/>
    public async Task<JsonElement> ExecuteNonQueryAsync(
        string connectionId,
        string sql,
        JsonElement? parameters = null,
        CancellationToken ct = default)
    {
        var (entry, conn) = OpenConnection(connectionId);
        await using var _ = conn;

        RequireWritable(entry, "ExecuteNonQuery");

        if (string.IsNullOrWhiteSpace(sql))
            throw new ArgumentException("SQL must not be empty.");

        await conn.OpenAsync(ct);
        var dp           = BuildParameters(parameters, entry.Provider);
        var rowsAffected = await conn.ExecuteAsync(
            new CommandDefinition(sql, dp, cancellationToken: ct));

        logger.LogInformation(
            "ExecuteNonQuery({Id}): {Rows} row(s) affected — {Sql}",
            connectionId, rowsAffected, TruncateForLog(sql));

        return Serialize(new { rowsAffected, sql = TruncateForLog(sql) });
    }

    /// <inheritdoc/>
    public async Task<JsonElement> CreateTableAsync(
        string connectionId,
        string tableName,
        string columnDefinitions,
        bool ifNotExists = true,
        CancellationToken ct = default)
    {
        var (entry, conn) = OpenConnection(connectionId);
        await using var _ = conn;

        RequireWritable(entry, "CreateTable");

        var quotedName = Quote(tableName, entry.Provider);
        string sql;

        if (!ifNotExists)
        {
            sql = $"CREATE TABLE {quotedName} ({columnDefinitions})";
        }
        else
        {
            sql = entry.Provider switch
            {
                // SQL Server / Azure SQL: IF NOT EXISTS … not supported — use OBJECT_ID guard
                DatabaseProvider.SqlServer or DatabaseProvider.AzureSQL =>
                    $"""
                    IF OBJECT_ID(N'{tableName.Replace("'", "''")}', N'U') IS NULL
                    BEGIN
                        CREATE TABLE {quotedName} ({columnDefinitions})
                    END
                    """,

                // Oracle < 23c: emulate via exception handler
                DatabaseProvider.Oracle =>
                    $"""
                    BEGIN
                        EXECUTE IMMEDIATE 'CREATE TABLE {quotedName} ({columnDefinitions.Replace("'", "''")})';
                    EXCEPTION
                        WHEN OTHERS THEN
                            IF SQLCODE != -955 THEN RAISE; END IF;
                    END;
                    """,

                // PostgreSQL, MySQL, MariaDB support the standard syntax
                _ => $"CREATE TABLE IF NOT EXISTS {quotedName} ({columnDefinitions})"
            };
        }

        await conn.OpenAsync(ct);
        await conn.ExecuteAsync(new CommandDefinition(sql, cancellationToken: ct));

        logger.LogInformation("CreateTable({Id}, {Table})", connectionId, tableName);
        return Serialize(new { success = true, table = tableName, sql });
    }

    /// <inheritdoc/>
    public async Task<JsonElement> DropTableAsync(
        string connectionId,
        string tableName,
        bool ifExists = true,
        CancellationToken ct = default)
    {
        var (entry, conn) = OpenConnection(connectionId);
        await using var _ = conn;

        RequireWritable(entry, "DropTable");

        string sql;
        if (ifExists && entry.Provider is DatabaseProvider.Oracle)
        {
            // Oracle < 23c: emulate IF EXISTS via PL/SQL exception handler
            sql = $"""
                BEGIN
                    EXECUTE IMMEDIATE 'DROP TABLE {Quote(tableName, entry.Provider)}';
                EXCEPTION
                    WHEN OTHERS THEN
                        IF SQLCODE != -942 THEN RAISE; END IF;
                END;
                """;
        }
        else
        {
            var guard = ifExists ? "IF EXISTS " : string.Empty;
            sql = $"DROP TABLE {guard}{Quote(tableName, entry.Provider)}";
        }

        await conn.OpenAsync(ct);
        await conn.ExecuteAsync(new CommandDefinition(sql, cancellationToken: ct));

        logger.LogInformation("DropTable({Id}, {Table})", connectionId, tableName);
        return Serialize(new { success = true, table = tableName });
    }

    /// <inheritdoc/>
    public async Task<JsonElement> TruncateTableAsync(
        string connectionId,
        string tableName,
        CancellationToken ct = default)
    {
        var (entry, conn) = OpenConnection(connectionId);
        await using var _ = conn;

        RequireWritable(entry, "TruncateTable");

        var sql = $"TRUNCATE TABLE {Quote(tableName, entry.Provider)}";

        await conn.OpenAsync(ct);
        await conn.ExecuteAsync(new CommandDefinition(sql, cancellationToken: ct));

        logger.LogInformation("TruncateTable({Id}, {Table})", connectionId, tableName);
        return Serialize(new { success = true, table = tableName });
    }

    /// <inheritdoc/>
    public async Task<JsonElement> AddColumnAsync(
        string connectionId,
        string tableName,
        string columnName,
        string columnDefinition,
        CancellationToken ct = default)
    {
        var (entry, conn) = OpenConnection(connectionId);
        await using var _ = conn;

        RequireWritable(entry, "AddColumn");

        // MySQL / MariaDB use ADD COLUMN; every other provider just uses ADD
        var keyword = entry.Provider is DatabaseProvider.MySQL or DatabaseProvider.MariaDB
            ? "ADD COLUMN"
            : "ADD";

        var sql = $"ALTER TABLE {Quote(tableName, entry.Provider)} {keyword} {Quote(columnName, entry.Provider)} {columnDefinition}";

        await conn.OpenAsync(ct);
        await conn.ExecuteAsync(new CommandDefinition(sql, cancellationToken: ct));

        logger.LogInformation("AddColumn({Id}, {Table}, {Col})", connectionId, tableName, columnName);
        return Serialize(new { success = true, table = tableName, column = columnName });
    }

    /// <inheritdoc/>
    public async Task<JsonElement> DropColumnAsync(
        string connectionId,
        string tableName,
        string columnName,
        CancellationToken ct = default)
    {
        var (entry, conn) = OpenConnection(connectionId);
        await using var _ = conn;

        RequireWritable(entry, "DropColumn");

        var sql = $"ALTER TABLE {Quote(tableName, entry.Provider)} DROP COLUMN {Quote(columnName, entry.Provider)}";

        await conn.OpenAsync(ct);
        await conn.ExecuteAsync(new CommandDefinition(sql, cancellationToken: ct));

        logger.LogInformation("DropColumn({Id}, {Table}, {Col})", connectionId, tableName, columnName);
        return Serialize(new { success = true, table = tableName, column = columnName });
    }

    /// <inheritdoc/>
    public async Task<JsonElement> RenameTableAsync(
        string connectionId,
        string oldTableName,
        string newTableName,
        CancellationToken ct = default)
    {
        var (entry, conn) = OpenConnection(connectionId);
        await using var _ = conn;

        RequireWritable(entry, "RenameTable");

        var sql = entry.Provider switch
        {
            DatabaseProvider.SqlServer or DatabaseProvider.AzureSQL
                => $"EXEC sp_rename '{oldTableName.Replace("'", "''")}', '{newTableName.Replace("'", "''")}'",

            DatabaseProvider.MySQL or DatabaseProvider.MariaDB
                => $"RENAME TABLE {Quote(oldTableName, entry.Provider)} TO {Quote(newTableName, entry.Provider)}",

            // PostgreSQL and Oracle
            _ => $"ALTER TABLE {Quote(oldTableName, entry.Provider)} RENAME TO {Quote(newTableName, entry.Provider)}"
        };

        await conn.OpenAsync(ct);
        await conn.ExecuteAsync(new CommandDefinition(sql, cancellationToken: ct));

        logger.LogInformation("RenameTable({Id}, {Old} → {New})", connectionId, oldTableName, newTableName);
        return Serialize(new { success = true, oldName = oldTableName, newName = newTableName });
    }

    /// <inheritdoc/>
    public async Task<JsonElement> TestConnectionAsync(
        string connectionId,
        CancellationToken ct = default)
    {
        var connections = settingsService.GetAsync(tenantContext.TenantId ?? Guid.Empty).GetAwaiter().GetResult().DatabaseConnections
                          ?? new(StringComparer.OrdinalIgnoreCase);

        if (!connections.TryGetValue(connectionId, out var entry))
            throw new KeyNotFoundException(
                $"Database connection '{connectionId}' is not configured. " +
                $"Available: {string.Join(", ", connections.Keys)}");

        if (!entry.IsEnabled)
            throw new InvalidOperationException($"Connection '{connectionId}' is disabled.");

        var sw   = Stopwatch.StartNew();
        await using var conn = CreateDbConnection(entry);
        await conn.OpenAsync(ct);
        sw.Stop();

        var probe = await ProbeAccessAsync(conn, entry.Provider, ct);

        // Determine effective access and an explanatory note
        string effectiveAccess;
        string? note = null;

        if (entry.ReadOnly)
        {
            effectiveAccess = "read-only (config)";
            if (!probe.DbLevelReadOnly && probe.UserCanInsert)
                note = "The database and DB user both allow writes, but this connection is " +
                       "configured as read-only in app settings (ReadOnly = true).";
        }
        else if (probe.DbLevelReadOnly)
        {
            effectiveAccess = "read-only (database)";
            note = "The database or server is in read-only mode (e.g. a read replica, " +
                   "snapshot, or Always On secondary)."; 
        }
        else if (!probe.UserCanInsert && !probe.UserCanUpdate && !probe.UserCanDelete)
        {
            effectiveAccess = "read-only (permissions)";
            note = "The DB user lacks INSERT, UPDATE, and DELETE privileges.";
        }
        else
        {
            effectiveAccess = "read-write";
        }

        var result = new
        {
            connectionId,
            label              = entry.Label,
            provider           = entry.Provider.ToString(),
            latencyMs          = sw.ElapsedMilliseconds,
            server             = probe.Server,
            database           = probe.Database,
            loginName          = probe.LoginName,
            dbUser             = probe.DbUser,
            serverVersion      = probe.ServerVersion,
            configuredReadOnly = entry.ReadOnly,
            dbLevelReadOnly    = probe.DbLevelReadOnly,
            userCanInsert      = probe.UserCanInsert,
            userCanUpdate      = probe.UserCanUpdate,
            userCanDelete      = probe.UserCanDelete,
            userCanCreateTable = probe.UserCanCreateTable,
            effectiveAccess,
            note,
        };

        logger.LogInformation(
            "TestConnection({Id}): {Latency}ms, effective={Access}",
            connectionId, sw.ElapsedMilliseconds, effectiveAccess);

        return Serialize(result);
    }

    // ── Access probes (provider-specific) ────────────────────────────────────

    private record ProbeResult(
        string LoginName,
        string DbUser,
        string Database,
        string Server,
        string ServerVersion,
        bool   DbLevelReadOnly,
        bool   UserCanInsert,
        bool   UserCanUpdate,
        bool   UserCanDelete,
        bool   UserCanCreateTable);

    private static Task<ProbeResult> ProbeAccessAsync(
        DbConnection conn, DatabaseProvider provider, CancellationToken ct) =>
        provider switch
        {
            DatabaseProvider.SqlServer or DatabaseProvider.AzureSQL
                => ProbeSqlServerAsync(conn, ct),
            DatabaseProvider.PostgreSQL
                => ProbePostgresAsync(conn, ct),
            DatabaseProvider.MySQL or DatabaseProvider.MariaDB
                => ProbeMySqlAsync(conn, ct),
            DatabaseProvider.Oracle
                => ProbeOracleAsync(conn, ct),
            _ => Task.FromResult(new ProbeResult(
                    "?", "?", "?", "?", "?",
                    false, false, false, false, false))
        };

    private static async Task<ProbeResult> ProbeSqlServerAsync(
        DbConnection conn, CancellationToken ct)
    {
        // HAS_PERMS_BY_NAME with NULL scope checks effective permissions on the current DB.
        // DATABASEPROPERTYEX('Updateability') detects read-only replicas / snapshots.
        const string sql =
            """
            SELECT
                SUSER_SNAME()                                                               AS loginname,
                USER_NAME()                                                                 AS dbuser,
                DB_NAME()                                                                   AS dbname,
                CAST(SERVERPROPERTY('ServerName')     AS NVARCHAR(256))                     AS servername,
                CAST(SERVERPROPERTY('ProductVersion') AS NVARCHAR(64))                      AS serverversion,
                CAST(DATABASEPROPERTYEX(DB_NAME(),'Updateability') AS NVARCHAR(20))         AS updateability,
                HAS_PERMS_BY_NAME(NULL,'DATABASE','INSERT')                                 AS caninsert,
                HAS_PERMS_BY_NAME(NULL,'DATABASE','UPDATE')                                 AS canupdate,
                HAS_PERMS_BY_NAME(NULL,'DATABASE','DELETE')                                 AS candelete,
                HAS_PERMS_BY_NAME(NULL,'DATABASE','CREATE TABLE')                           AS cancreatetable
            """;

        var r = (IDictionary<string, object>)await conn.QueryFirstAsync<dynamic>(
            new CommandDefinition(sql, cancellationToken: ct));

        bool IsOne(string key) => r.TryGetValue(key, out var v) && Convert.ToInt32(v) == 1;
        string Str(string key) => r.TryGetValue(key, out var v) ? v?.ToString() ?? "" : "";

        return new ProbeResult(
            LoginName:          Str("loginname"),
            DbUser:             Str("dbuser"),
            Database:           Str("dbname"),
            Server:             Str("servername"),
            ServerVersion:      Str("serverversion"),
            DbLevelReadOnly:    string.Equals(Str("updateability"), "READ_ONLY",
                                    StringComparison.OrdinalIgnoreCase),
            UserCanInsert:      IsOne("caninsert"),
            UserCanUpdate:      IsOne("canupdate"),
            UserCanDelete:      IsOne("candelete"),
            UserCanCreateTable: IsOne("cancreatetable"));
    }

    private static async Task<ProbeResult> ProbePostgresAsync(
        DbConnection conn, CancellationToken ct)
    {
        // pg_is_in_recovery() is true on streaming/logical standbys (read replicas).
        // has_schema_privilege checks whether the user can CREATE objects in the public schema.
        const string sql =
            """
            SELECT
                current_user                                                    AS loginname,
                current_database()                                              AS dbname,
                COALESCE(inet_server_addr()::TEXT, 'localhost')                 AS servername,
                current_setting('server_version')                               AS serverversion,
                pg_is_in_recovery()                                             AS isreplica,
                has_database_privilege(current_database(),'CREATE')             AS cancreatedb,
                has_schema_privilege('public','CREATE')                         AS cancreate
            """;

        var r = (IDictionary<string, object>)await conn.QueryFirstAsync<dynamic>(
            new CommandDefinition(sql, cancellationToken: ct));

        bool Flag(string key) => r.TryGetValue(key, out var v) && Convert.ToBoolean(v);
        string Str(string key) => r.TryGetValue(key, out var v) ? v?.ToString() ?? "" : "";

        bool canWrite = Flag("cancreate");
        return new ProbeResult(
            LoginName:          Str("loginname"),
            DbUser:             Str("loginname"),
            Database:           Str("dbname"),
            Server:             Str("servername"),
            ServerVersion:      Str("serverversion"),
            DbLevelReadOnly:    Flag("isreplica"),
            UserCanInsert:      canWrite,
            UserCanUpdate:      canWrite,
            UserCanDelete:      canWrite,
            UserCanCreateTable: canWrite);
    }

    private static async Task<ProbeResult> ProbeMySqlAsync(
        DbConnection conn, CancellationToken ct)
    {
        // @@read_only is ON for read replicas and when SET READ_ONLY has been used.
        const string sql =
            """
            SELECT
                CURRENT_USER()   AS loginname,
                DATABASE()       AS dbname,
                @@hostname       AS servername,
                VERSION()        AS serverversion,
                @@read_only      AS readonly_flag
            """;

        var r = (IDictionary<string, object>)await conn.QueryFirstAsync<dynamic>(
            new CommandDefinition(sql, cancellationToken: ct));

        bool   dbRo  = r.TryGetValue("readonly_flag", out var roVal) && Convert.ToInt32(roVal) != 0;
        string Str(string key) => r.TryGetValue(key, out var v) ? v?.ToString() ?? "" : "";

        return new ProbeResult(
            LoginName:          Str("loginname"),
            DbUser:             Str("loginname"),
            Database:           Str("dbname"),
            Server:             Str("servername"),
            ServerVersion:      Str("serverversion"),
            DbLevelReadOnly:    dbRo,
            UserCanInsert:      !dbRo,
            UserCanUpdate:      !dbRo,
            UserCanDelete:      !dbRo,
            UserCanCreateTable: !dbRo);
    }

    private static async Task<ProbeResult> ProbeOracleAsync(
        DbConnection conn, CancellationToken ct)
    {
        const string sql =
            """
            SELECT
                SYS_CONTEXT('USERENV','SESSION_USER')  AS loginname,
                SYS_CONTEXT('USERENV','CURRENT_USER')  AS dbuser,
                SYS_CONTEXT('USERENV','DB_NAME')       AS dbname,
                SYS_CONTEXT('USERENV','HOST')          AS servername,
                (SELECT BANNER FROM V$VERSION WHERE ROWNUM = 1) AS serverversion
            FROM DUAL
            """;

        var r = (IDictionary<string, object>)await conn.QueryFirstAsync<dynamic>(
            new CommandDefinition(sql, cancellationToken: ct));

        string Str(string key) => r.TryGetValue(key, out var v) ? v?.ToString() ?? "" : "";

        // Check CREATE TABLE privilege from the data dictionary
        int createPriv = 0;
        try
        {
            createPriv = await conn.ExecuteScalarAsync<int>(new CommandDefinition(
                "SELECT COUNT(*) FROM USER_SYS_PRIVS WHERE PRIVILEGE = 'CREATE TABLE'",
                cancellationToken: ct));
        }
        catch { /* low-privilege user may not have access to USER_SYS_PRIVS */ }

        return new ProbeResult(
            LoginName:          Str("loginname"),
            DbUser:             Str("dbuser"),
            Database:           Str("dbname"),
            Server:             Str("servername"),
            ServerVersion:      Str("serverversion"),
            DbLevelReadOnly:    false,  // Needs V$DATABASE; assume writable
            UserCanInsert:      true,   // Oracle: granular check needs per-table query
            UserCanUpdate:      true,
            UserCanDelete:      true,
            UserCanCreateTable: createPriv > 0);
    }

    // ── Connection factory ──────────────────────────────────────────────────

    private (DatabaseConnectionEntry entry, DbConnection conn) OpenConnection(string connectionId)
    {
        var connections = settingsService.GetAsync(tenantContext.TenantId ?? Guid.Empty).GetAwaiter().GetResult().DatabaseConnections
                          ?? new(StringComparer.OrdinalIgnoreCase);

        if (!connections.TryGetValue(connectionId, out var entry))
            throw new KeyNotFoundException(
                $"Database connection '{connectionId}' is not configured. " +
                $"Available: {string.Join(", ", connections.Keys)}");

        if (!entry.IsEnabled)
            throw new InvalidOperationException(
                $"Database connection '{connectionId}' is currently disabled.");

        var conn = CreateDbConnection(entry);
        return (entry, conn);
    }

    private static DbConnection CreateDbConnection(DatabaseConnectionEntry entry) =>
        entry.Provider switch
        {
            // SQL Server & Azure SQL use the same driver.
            // Azure AD / Managed Identity auth is configured via the connection string:
            //   "Authentication=Active Directory Default"  — works for Managed Identity, user, SP
            //   "Authentication=Active Directory Managed Identity" — explicit MSI
            DatabaseProvider.SqlServer or DatabaseProvider.AzureSQL
                => new SqlConnection(entry.ConnectionString),

            // PostgreSQL — also used for AWS RDS PG, Aurora PG, Azure Database for PostgreSQL.
            // Add SSL: "SslMode=Require;Trust Server Certificate=true" for RDS/Azure.
            DatabaseProvider.PostgreSQL
                => new NpgsqlConnection(entry.ConnectionString),

            // MySQL — also used for AWS RDS MySQL, Aurora MySQL, Azure Database for MySQL.
            // MySqlConnector is async-first and supports IAM auth via AWS plugins.
            DatabaseProvider.MySQL or DatabaseProvider.MariaDB
                => new MySqlConnection(entry.ConnectionString),

            // Oracle on-premises or OCI cloud.
            // Note: Oracle uses ':param' placeholders, not '@param'.
            DatabaseProvider.Oracle
                => new OracleConnection(entry.ConnectionString),

            _ => throw new NotSupportedException($"Provider '{entry.Provider}' is not supported.")
        };

    // ── Query safety ──────────────────────────────────────────────────────────

    private static void ValidateQuery(string query, DatabaseConnectionEntry entry)
    {
        if (string.IsNullOrWhiteSpace(query))
            throw new ArgumentException("Query must not be empty.");

        if (!entry.ReadOnly) return;

        var trimmed = query.TrimStart().ToUpperInvariant();
        if (!trimmed.StartsWith("SELECT", StringComparison.Ordinal) &&
            !trimmed.StartsWith("WITH",   StringComparison.Ordinal) &&
            !trimmed.StartsWith("EXEC",   StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"Connection '{entry.Label}' is read-only. " +
                "Only SELECT, WITH (CTE), and EXEC queries are permitted.");
        }
    }

    /// <summary>
    /// Wraps the query in a provider-specific row limit when the query does not
    /// already contain a TOP / LIMIT / FETCH / ROWNUM clause.
    /// A trailing top-level ORDER BY is lifted out of the subquery and re-attached
    /// to the outer SELECT so that SQL Server does not reject it.
    /// </summary>
    private static string ApplyRowLimit(string query, DatabaseProvider provider, int maxRows)
    {
        var upper = query.ToUpperInvariant();

        // Bail out if the query already has an explicit limit
        if (upper.Contains(" TOP ") || upper.Contains(" LIMIT ") ||
            upper.Contains(" FETCH ") || upper.Contains(" ROWNUM "))
            return query;

        // Lift any trailing top-level ORDER BY out of the subquery.
        // SQL Server rejects ORDER BY inside a derived table unless TOP/OFFSET/FOR XML is present.
        var orderByIndex  = FindTrailingOrderBy(upper);
        var innerQuery    = orderByIndex >= 0 ? query[..orderByIndex].TrimEnd() : query;
        var orderByClause = orderByIndex >= 0 ? query[orderByIndex..] : string.Empty;

        return provider switch
        {
            DatabaseProvider.SqlServer or DatabaseProvider.AzureSQL
                => $"SELECT TOP {maxRows} * FROM ({innerQuery}) __q__ {orderByClause}".TrimEnd(),

            DatabaseProvider.Oracle
                => $"SELECT * FROM ({innerQuery}) __q__ WHERE ROWNUM <= {maxRows}",

            // PostgreSQL, MySQL, MariaDB
            _ => $"SELECT * FROM ({innerQuery}) AS __q__ {orderByClause} LIMIT {maxRows}".TrimEnd()
        };
    }

    /// <summary>
    /// Returns the string index of the last top-level ORDER BY clause in
    /// <paramref name="upperQuery"/> (already upper-cased), or -1 if none exists.
    /// Ignores ORDER BY that appears inside parentheses.
    /// </summary>
    private static int FindTrailingOrderBy(string upperQuery)
    {
        int depth = 0;
        int lastOrderBy = -1;
        for (int i = 0; i < upperQuery.Length; i++)
        {
            if (upperQuery[i] == '(') { depth++; continue; }
            if (upperQuery[i] == ')') { depth--; continue; }
            if (depth == 0 && char.IsWhiteSpace(upperQuery[i]) &&
                upperQuery.AsSpan(i + 1).StartsWith("ORDER BY ", StringComparison.Ordinal))
                lastOrderBy = i;
        }
        return lastOrderBy;
    }

    // ── Parameter building ────────────────────────────────────────────────────

    /// <summary>
    /// Converts a flat JSON object like <c>{"name":"Alice","age":30}</c>
    /// into Dapper <see cref="DynamicParameters"/>.
    /// Oracle parameter names are prefixed with ':'; all others use '@'.
    /// </summary>
    private static DynamicParameters? BuildParameters(JsonElement? json, DatabaseProvider provider)
    {
        if (json is not { ValueKind: JsonValueKind.Object } obj) return null;

        var dp = new DynamicParameters();
        foreach (var prop in obj.EnumerateObject())
        {
            // Oracle expects ':name' — all other providers expect '@name'
            var name = provider is DatabaseProvider.Oracle
                ? prop.Name.TrimStart(':')
                : prop.Name.TrimStart('@');

            object? value = prop.Value.ValueKind switch
            {
                JsonValueKind.String  => prop.Value.GetString(),
                JsonValueKind.Number  => prop.Value.TryGetInt64(out var i) ? (object)i : prop.Value.GetDouble(),
                JsonValueKind.True    => true,
                JsonValueKind.False   => false,
                JsonValueKind.Null    => null,
                _                    => prop.Value.GetRawText()  // nested objects/arrays as raw JSON strings
            };
            dp.Add(name, value);
        }
        return dp;
    }

    // ── Metadata SQL (provider-specific) ─────────────────────────────────────

    private static string ListTablesSql(DatabaseProvider provider) => provider switch
    {
        DatabaseProvider.PostgreSQL =>
            """
            SELECT table_schema AS "tableSchema",
                   table_name   AS "tableName",
                   table_type   AS "tableType"
            FROM   information_schema.tables
            WHERE  table_schema NOT IN ('pg_catalog', 'information_schema')
            ORDER  BY table_schema, table_name
            """,

        DatabaseProvider.MySQL or DatabaseProvider.MariaDB =>
            """
            SELECT TABLE_SCHEMA AS tableSchema,
                   TABLE_NAME   AS tableName,
                   TABLE_TYPE   AS tableType
            FROM   INFORMATION_SCHEMA.TABLES
            WHERE  TABLE_SCHEMA = DATABASE()
            ORDER  BY TABLE_NAME
            """,

        DatabaseProvider.Oracle =>
            """
            SELECT OWNER      AS "tableSchema",
                   TABLE_NAME AS "tableName",
                   'TABLE'    AS "tableType"
            FROM   ALL_TABLES
            ORDER  BY OWNER, TABLE_NAME
            """,

        // SqlServer / AzureSQL (ANSI INFORMATION_SCHEMA)
        _ =>
            """
            SELECT TABLE_SCHEMA AS tableSchema,
                   TABLE_NAME   AS tableName,
                   TABLE_TYPE   AS tableType
            FROM   INFORMATION_SCHEMA.TABLES
            ORDER  BY TABLE_SCHEMA, TABLE_NAME
            """
    };

    private static (string sql, object param) SchemaSql(DatabaseProvider provider, string tableName)
    {
        if (provider is DatabaseProvider.Oracle)
        {
            return (
                """
                SELECT COLUMN_NAME   AS "columnName",
                       DATA_TYPE     AS "dataType",
                       NULLABLE      AS "isNullable",
                       DATA_LENGTH   AS "maxLength",
                       DATA_DEFAULT  AS "defaultValue"
                FROM   ALL_TAB_COLUMNS
                WHERE  TABLE_NAME = :tableName
                ORDER  BY COLUMN_ID
                """,
                new { tableName = tableName.ToUpperInvariant() });
        }

        // ANSI INFORMATION_SCHEMA works for SQL Server, Azure SQL, PostgreSQL, MySQL, MariaDB
        return (
            """
            SELECT COLUMN_NAME             AS columnName,
                   DATA_TYPE               AS dataType,
                   IS_NULLABLE             AS isNullable,
                   CHARACTER_MAXIMUM_LENGTH AS maxLength,
                   COLUMN_DEFAULT          AS defaultValue
            FROM   INFORMATION_SCHEMA.COLUMNS
            WHERE  TABLE_NAME = @tableName
            ORDER  BY ORDINAL_POSITION
            """,
            new { tableName });
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    /// <summary>
    /// Throws when the connection is marked read-only, preventing accidental writes.
    /// </summary>
    private static void RequireWritable(DatabaseConnectionEntry entry, string operation)
    {
        if (entry.ReadOnly)
            throw new InvalidOperationException(
                $"Connection '{entry.Label}' is marked read-only. " +
                $"'{operation}' requires a read-write connection. " +
                "Set ReadOnly = false in the connection configuration to enable write operations.");
    }

    /// <summary>
    /// Wraps an identifier in the provider-appropriate quote characters:
    /// <c>[brackets]</c> for SQL Server / Azure SQL,
    /// <c>`backticks`</c> for MySQL / MariaDB,
    /// <c>"double quotes"</c> for PostgreSQL and Oracle.
    /// </summary>
    private static string Quote(string name, DatabaseProvider provider) => provider switch
    {
        DatabaseProvider.SqlServer or DatabaseProvider.AzureSQL
            => $"[{name.Replace("]", "]]")}]",

        DatabaseProvider.MySQL or DatabaseProvider.MariaDB
            => $"`{name.Replace("`", "``")}`",

        // PostgreSQL preserves case; Oracle folds to upper without quotes
        _ => $"\"{name.Replace("\"", "\"\"")}\""
    };

    private static JsonElement Serialize<T>(T value)
    {
        var json = JsonSerializer.Serialize(value, SerializerOpts);
        return JsonDocument.Parse(json).RootElement.Clone();
    }

    private static string TruncateForLog(string s) =>
        s.Length > 120 ? string.Concat(s.AsSpan(0, 120), "…") : s;
}
