using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace erp_inteligente.Core.Engine;

/// <summary>
/// Stable JSON form of a <see cref="PlanDiff"/>, stored in <c>plans.simulated_diff_json</c>. The document carries
/// <c>"v":1</c>; anything else (the legacy seed shape <c>{"ABC-123":{"DOCA-01":-40}}</c>, malformed text) reads as an empty diff.
/// </summary>
public static class PlanDiffJson
{
    private const int Version = 1;

    /// <summary>Writes the diff as compact JSON with a fixed property order.</summary>
    public static string Serialize(PlanDiff diff)
    {
        ArgumentNullException.ThrowIfNull(diff);
        var root = new JsonObject
        {
            ["v"] = Version,
            ["balances"] = Array(diff.Balances, Balance),
            ["direct_balances"] = Array(diff.DirectBalances, Balance),
            ["moves"] = Array(diff.Moves, m => new JsonObject
            {
                ["sku"] = m.Sku, ["lot"] = m.Lot, ["from"] = m.From, ["to"] = m.To, ["qty"] = m.Quantity, ["unit"] = m.Unit,
            }),
            ["created"] = Array(diff.Created, c => new JsonObject { ["kind"] = c.Kind, ["code"] = c.Code, ["description"] = c.Description }),
            ["receipts"] = Array(diff.Receipts, Quantity),
            ["adjustments"] = Array(diff.Adjustments, Quantity),
        };
        return root.ToJsonString();
    }

    /// <summary>Reads a stored diff. Never throws: unknown or malformed text yields an empty diff.</summary>
    public static PlanDiff Deserialize(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return Empty();
        try
        {
            if (JsonNode.Parse(json) is not JsonObject root || root["v"]?.GetValue<int>() != Version)
                return Empty();
            return new PlanDiff(
                Read(root, "balances", ReadBalance),
                Read(root, "direct_balances", ReadBalance),
                Read(root, "moves", n => new MoveDelta(
                    n["sku"]!.GetValue<string>(), n["lot"]?.GetValue<string>(), n["from"]!.GetValue<string>(),
                    n["to"]!.GetValue<string>(), n["qty"]!.GetValue<int>(), n["unit"]!.GetValue<string>())),
                Read(root, "created", n => new CreatedRecord(
                    n["kind"]!.GetValue<string>(), n["code"]!.GetValue<string>(), n["description"]!.GetValue<string>())),
                Read(root, "receipts", ReadQuantity),
                Read(root, "adjustments", ReadQuantity));
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or FormatException or NullReferenceException)
        {
            return Empty();
        }
    }

    private static PlanDiff Empty() => new([], [], [], [], [], []);

    private static JsonObject Balance(BalanceDelta b) => new()
    {
        ["sku"] = b.Sku, ["location"] = b.Location, ["lot"] = b.Lot, ["before"] = b.Before, ["after"] = b.After, ["unit"] = b.Unit,
    };

    private static JsonObject Quantity(QuantityDelta q) => new() { ["sku"] = q.Sku, ["qty"] = q.Quantity, ["unit"] = q.Unit };

    private static BalanceDelta ReadBalance(JsonNode n) => new(
        n["sku"]!.GetValue<string>(), n["location"]!.GetValue<string>(), n["lot"]?.GetValue<string>(),
        n["before"]!.GetValue<int>(), n["after"]!.GetValue<int>(), n["unit"]!.GetValue<string>());

    private static QuantityDelta ReadQuantity(JsonNode n) =>
        new(n["sku"]!.GetValue<string>(), n["qty"]!.GetValue<int>(), n["unit"]!.GetValue<string>());

    private static JsonArray Array<T>(IReadOnlyList<T> items, Func<T, JsonNode> map)
    {
        var array = new JsonArray();
        foreach (var item in items) array.Add(map(item));
        return array;
    }

    private static List<T> Read<T>(JsonObject root, string name, Func<JsonNode, T> map)
    {
        var list = new List<T>();
        if (root[name] is not JsonArray array) return list;
        foreach (var node in array)
            list.Add(map(node ?? throw new JsonException("null item")));
        return list;
    }
}
