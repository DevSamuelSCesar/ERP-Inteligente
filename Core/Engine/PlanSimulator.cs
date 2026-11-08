using System;
using erp_inteligente.Core.Data;
using erp_inteligente.Models;

namespace erp_inteligente.Core.Engine;

/// <summary>Dry-runs a plan in a transaction that is always rolled back: nothing persists.</summary>
public sealed class PlanSimulator(NexusDatabase database, TimeProvider time)
{
    public RunResult Simulate(UserSession requester, PlanDocument plan, Func<string, bool> isAllowed)
    {
        ArgumentNullException.ThrowIfNull(requester);
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(isAllowed);

        using var connection = database.OpenExecutor();
        using var cipher = database.CreateColumnCipher();
        using var transaction = connection.BeginTransaction(deferred: false);
        try
        {
            var ctx = new EngineContext(connection, transaction, requester, null, time, cipher);
            return PlanRunner.Run(ctx, plan, isAllowed);
        }
        finally
        {
            transaction.Rollback();
        }
    }
}
