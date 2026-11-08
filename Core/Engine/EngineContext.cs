using System;
using System.Collections.Generic;
using erp_inteligente.Core.Audit;
using erp_inteligente.Core.Security;
using erp_inteligente.Models;
using Microsoft.Data.Sqlite;

namespace erp_inteligente.Core.Engine;

/// <summary>Everything a handler needs while a plan is simulated or executed inside one transaction.</summary>
public sealed class EngineContext
{
    private readonly Dictionary<string, string> _results = new(StringComparer.Ordinal);

    public EngineContext(
        SqliteConnection connection, SqliteTransaction transaction, UserSession requester,
        long? planId, TimeProvider time, ColumnCipher cipher)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(transaction);
        ArgumentNullException.ThrowIfNull(requester);
        ArgumentNullException.ThrowIfNull(time);
        ArgumentNullException.ThrowIfNull(cipher);
        Connection = connection;
        Transaction = transaction;
        Requester = requester;
        WarehouseId = requester.WarehouseId;
        PlanId = planId;
        Timestamp = AuditHasher.FormatTimestamp(time.GetUtcNow());
        Cipher = cipher;
    }

    public SqliteConnection Connection { get; }
    public SqliteTransaction Transaction { get; }
    public UserSession Requester { get; }
    public long WarehouseId { get; }

    /// <summary>The plan row id; <c>null</c> in a simulation.</summary>
    public long? PlanId { get; }

    /// <summary>UTC timestamp (audit format) used for every row written by the run.</summary>
    public string Timestamp { get; }

    public ColumnCipher Cipher { get; }

    /// <summary>Step results by <c>as</c> name, used to resolve <c>$name</c> references.</summary>
    public Dictionary<string, string> Results => _results;

    public PlanDiffBuilder Diff { get; } = new();

    /// <summary>A command bound to the run's transaction. Callers must add parameters, never concatenate values.</summary>
    internal SqliteCommand Command(string sql)
    {
        var command = Connection.CreateCommand();
        command.Transaction = Transaction;
        command.CommandText = sql;
        return command;
    }
}
