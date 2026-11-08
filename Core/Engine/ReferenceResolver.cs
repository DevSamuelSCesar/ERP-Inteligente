using System;
using System.Collections.Generic;
using System.Text.Json.Nodes;

namespace erp_inteligente.Core.Engine;

/// <summary>Replaces <c>$name</c> references in step arguments with the results of earlier steps.</summary>
public static class ReferenceResolver
{
    /// <summary>
    /// Returns a deep copy of the step arguments with every <c>$name</c> string replaced by <c>results[name]</c>.
    /// The original arguments are not modified.
    /// </summary>
    /// <exception cref="InvalidOperationException">A referenced result is not available.</exception>
    public static JsonObject Resolve(PlanStepDocument step, IReadOnlyDictionary<string, string> results)
    {
        ArgumentNullException.ThrowIfNull(step);
        ArgumentNullException.ThrowIfNull(results);
        return (JsonObject)ResolveNode(step.Args, results)!;
    }

    private static JsonNode? ResolveNode(JsonNode? node, IReadOnlyDictionary<string, string> results)
    {
        switch (node)
        {
            case null:
                return null;
            case JsonObject o:
                var copy = new JsonObject();
                foreach (var (name, child) in o)
                    copy[name] = ResolveNode(child, results);
                return copy;
            case JsonArray a:
                var list = new JsonArray();
                foreach (var child in a)
                    list.Add(ResolveNode(child, results));
                return list;
            default:
                if (node is JsonValue v && v.TryGetValue<string>(out var text) && DslParser.ReferencePattern().IsMatch(text))
                {
                    if (!results.TryGetValue(text[1..], out var value))
                        throw new InvalidOperationException("A referência não tem resultado disponível.");
                    return JsonValue.Create(value);
                }
                return node.DeepClone();
        }
    }
}
