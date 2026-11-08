using System.Collections.Generic;
using System.Text.Json.Nodes;

namespace erp_inteligente.Core.Engine;

/// <summary>One parsed step. <see cref="Args"/> are still untrusted until <see cref="SchemaValidator"/> accepted them.</summary>
public sealed record PlanStepDocument(int Index, string Op, JsonObject Args, string? As);

/// <summary>A parsed DSL v1 plan (references/ARQUITETURA.md, section 5).</summary>
public sealed record PlanDocument(string DslVersion, string Intent, string? Skill, IReadOnlyList<PlanStepDocument> Steps)
{
    /// <summary>The canonical DSL JSON (always with <c>dsl_version</c>), as stored in <c>plans.plan_json</c>.</summary>
    public string ToJson()
    {
        var steps = new JsonArray();
        foreach (var step in Steps)
        {
            var node = new JsonObject { ["op"] = step.Op, ["args"] = step.Args.DeepClone() };
            if (step.As is not null)
                node["as"] = step.As;
            steps.Add(node);
        }
        var root = new JsonObject
        {
            ["dsl_version"] = DslVersion,
            ["intent"] = Intent,
            ["skill"] = Skill,
            ["steps"] = steps,
        };
        return root.ToJsonString();
    }
}
