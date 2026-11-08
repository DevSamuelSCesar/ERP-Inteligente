using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;

namespace erp_inteligente.Core.Engine;

/// <summary>Outcome of running a plan: either errors (nothing valid to apply) or the diff of what was done.</summary>
public sealed record RunResult(IReadOnlyList<PlanError> Errors, PlanDiff? Diff, RiskLevel Risk)
{
    public bool Succeeded => Errors.Count == 0;
}

/// <summary>
/// Runs a parsed plan inside a caller-owned transaction: schema, permissions, then step by step
/// (resolve references, validate against the current state, apply). The caller commits or rolls back.
/// </summary>
public static class PlanRunner
{
    private static readonly Dictionary<string, IOperationHandler> Handlers = new IOperationHandler[]
    {
        new ReceiveGoodsHandler(), new MoveStockHandler(), new AdjustStockHandler(),
        new CreateProductHandler(), new CreateLocationHandler(),
    }.ToDictionary(h => h.Op, StringComparer.Ordinal);

    /// <param name="isAllowed">Whether the requester currently holds a permission code.</param>
    public static RunResult Run(EngineContext ctx, PlanDocument plan, Func<string, bool> isAllowed)
    {
        ArgumentNullException.ThrowIfNull(ctx);
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(isAllowed);

        var schemaErrors = SchemaValidator.Validate(plan);
        if (schemaErrors.Count > 0)
            return new RunResult(schemaErrors, null, RiskLevel.Read);

        var risk = RiskLevels.Max(plan.Steps.Select(s => OperationCatalog.TryGet(s.Op, out var d) ? d.Risk : RiskLevel.Read));

        var denied = new List<PlanError>();
        foreach (var step in plan.Steps)
        {
            OperationCatalog.TryGet(step.Op, out var definition);
            if (!isAllowed(definition.Permission))
                denied.Add(new PlanError(step.Index, null, "policy.denied", $"Passo {step.Index + 1}: seu papel não permite {definition.Label}."));
        }
        if (denied.Count > 0)
            return new RunResult(denied, null, risk);

        foreach (var step in plan.Steps)
        {
            if (!Handlers.TryGetValue(step.Op, out var handler))
                return new RunResult([new PlanError(step.Index, "op", "dsl.unknown_op", $"Passo {step.Index + 1}: operação desconhecida.")], null, risk);

            JsonObject args;
            try
            {
                args = ReferenceResolver.Resolve(step, ctx.Results);
            }
            catch (InvalidOperationException)
            {
                return new RunResult([new PlanError(step.Index, null, "plan.reference",
                    $"Passo {step.Index + 1}: o resultado de um passo anterior não está disponível.")], null, risk);
            }

            var errors = handler.Validate(ctx, step, args);
            if (errors.Count > 0)
                return new RunResult(errors, null, risk);

            var result = handler.Apply(ctx, step, args);
            if (step.As is not null)
                ctx.Results[step.As] = result;
        }
        return new RunResult([], ctx.Diff.Build(), risk);
    }
}
