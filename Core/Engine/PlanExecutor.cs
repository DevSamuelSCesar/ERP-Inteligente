using System;
using erp_inteligente.Core.Data;
using erp_inteligente.Models;
using Microsoft.Data.Sqlite;

namespace erp_inteligente.Core.Engine;

/// <summary>Executes a plan all or nothing in one immediate transaction.</summary>
public sealed class PlanExecutor(NexusDatabase database, TimeProvider time)
{
    /// <summary>
    /// Runs the plan. On success <paramref name="onSuccess"/> runs in the same transaction (the engine records the plan and
    /// the audit there) and everything commits together; if it throws, everything is rolled back and the exception rethrown.
    /// On plan errors the plan effects are rolled back and <paramref name="onFailure"/> runs in a new transaction.
    /// </summary>
    public RunResult Execute(
        UserSession requester, PlanDocument plan, long planId, Func<string, bool> isAllowed,
        Action<SqliteConnection, SqliteTransaction, RunResult> onSuccess,
        Action<SqliteConnection, SqliteTransaction, RunResult>? onFailure = null)
    {
        ArgumentNullException.ThrowIfNull(requester);
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(isAllowed);
        ArgumentNullException.ThrowIfNull(onSuccess);

        using var connection = database.OpenExecutor();
        using var cipher = database.CreateColumnCipher();
        RunResult result;
        using (var transaction = connection.BeginTransaction(deferred: false))
        {
            try
            {
                var ctx = new EngineContext(connection, transaction, requester, planId, time, cipher);
                result = PlanRunner.Run(ctx, plan, isAllowed);
                if (result.Succeeded)
                {
                    onSuccess(connection, transaction, result);
                    transaction.Commit();
                    return result;
                }
                transaction.Rollback();
            }
            catch
            {
                transaction.Rollback();
                throw;
            }
        }

        if (onFailure is not null)
        {
            using var failure = connection.BeginTransaction(deferred: false);
            try
            {
                onFailure(connection, failure, result);
                failure.Commit();
            }
            catch
            {
                failure.Rollback();
                throw;
            }
        }
        return result;
    }
}
