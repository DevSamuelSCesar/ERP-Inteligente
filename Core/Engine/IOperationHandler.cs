using System.Collections.Generic;
using System.Text.Json.Nodes;

namespace erp_inteligente.Core.Engine;

/// <summary>
/// Business rules and effects of one plan operation. Arguments are already schema-valid and reference-resolved,
/// but still untrusted values: handlers only use them as SQL parameters.
/// </summary>
public interface IOperationHandler
{
    /// <summary>The DSL operation name.</summary>
    string Op { get; }

    /// <summary>Checks the step against the CURRENT transaction state (so earlier steps count). Writes nothing.</summary>
    IReadOnlyList<PlanError> Validate(EngineContext ctx, PlanStepDocument step, JsonObject args);

    /// <summary>Applies a validated step and returns its result value (the SKU or the location code) for <c>as</c>.</summary>
    string Apply(EngineContext ctx, PlanStepDocument step, JsonObject args);
}
