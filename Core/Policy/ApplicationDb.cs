using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using erp_inteligente.Core.Data;
using erp_inteligente.Models;
using Microsoft.Data.Sqlite;

namespace erp_inteligente.Core.Policy;

/// <summary>
/// The only way screens read data (references/ARQUITETURA.md, section 12.6). Each call checks the permission,
/// scopes the statement to the session's warehouse and runs it on a fresh application connection that is
/// switched to <c>query_only</c>, so even a crafted <c>WITH ... DELETE</c> cannot write.
/// </summary>
public sealed class ApplicationDb(NexusDatabase database, PolicyEngine policy)
{
    private const string WarehouseParameter = "$warehouse_id";
    private static readonly Regex WarehouseRegex = new(@"\$warehouse_id\b", RegexOptions.CultureInvariant);
    private static readonly Regex ReadStart = new(@"^(SELECT|WITH)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    /// <summary>
    /// Runs a read that must be scoped to the user's warehouse. The SQL must contain <c>$warehouse_id</c>, which this
    /// method binds; callers cannot bind it themselves.
    /// </summary>
    public async Task<IReadOnlyList<T>> QueryScopedAsync<T>(
        UserSession session, string permission, string sql, Func<SqliteDataReader, T> map,
        Action<SqliteParameterCollection>? bind = null, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(map);
        var statement = ValidateSingleRead(sql);
        if (!WarehouseRegex.IsMatch(statement))
            throw new ArgumentException("A scoped query must use the $warehouse_id parameter.", nameof(sql));

        policy.Require(session, permission);
        return await RunAsync(statement, map, bind, session.WarehouseId, ct).ConfigureAwait(false);
    }

    /// <summary>Runs a read of catalog data that has no warehouse. The SQL must not mention <c>$warehouse_id</c>.</summary>
    public async Task<IReadOnlyList<T>> QueryGlobalAsync<T>(
        UserSession session, string permission, string sql, Func<SqliteDataReader, T> map,
        Action<SqliteParameterCollection>? bind = null, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(map);
        var statement = ValidateSingleRead(sql);
        if (WarehouseRegex.IsMatch(statement))
            throw new ArgumentException("A global query must not use $warehouse_id; use QueryScopedAsync.", nameof(sql));

        policy.Require(session, permission);
        return await RunAsync(statement, map, bind, null, ct).ConfigureAwait(false);
    }

    /// <summary>Accepts one SELECT/WITH statement with at most a trailing semicolon; returns it trimmed.</summary>
    private static string ValidateSingleRead(string sql)
    {
        ArgumentNullException.ThrowIfNull(sql);
        var statement = sql.Trim();
        if (statement.EndsWith(';')) statement = statement[..^1].TrimEnd();
        if (!ReadStart.IsMatch(statement))
            throw new ArgumentException("Only SELECT or WITH statements are allowed.", nameof(sql));
        if (statement.Contains(';', StringComparison.Ordinal))
            throw new ArgumentException("Only a single statement is allowed.", nameof(sql));
        return statement;
    }

    private async Task<IReadOnlyList<T>> RunAsync<T>(
        string sql, Func<SqliteDataReader, T> map, Action<SqliteParameterCollection>? bind,
        long? warehouseId, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        await using var connection = database.OpenApplication();
        using (var pragma = connection.CreateCommand())
        {
            pragma.CommandText = "PRAGMA query_only = ON;";
            pragma.ExecuteNonQuery();
        }

        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        bind?.Invoke(command.Parameters);
        foreach (SqliteParameter parameter in command.Parameters)
        {
            if (string.Equals(parameter.ParameterName.TrimStart('$', '@', ':'), "warehouse_id", StringComparison.Ordinal))
                throw new ArgumentException("The $warehouse_id parameter is bound by the policy engine.", nameof(bind));
        }
        if (warehouseId is { } id) command.Parameters.AddWithValue(WarehouseParameter, id);

        var rows = new List<T>();
        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false)) rows.Add(map(reader));
        return rows;
    }
}
