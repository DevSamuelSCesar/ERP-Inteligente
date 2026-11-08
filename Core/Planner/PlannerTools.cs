using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using erp_inteligente.Core.Data;
using erp_inteligente.Core.Engine;
using erp_inteligente.Core.Llm;
using erp_inteligente.Core.Skills;
using erp_inteligente.Models;
using Json.Schema;
using Microsoft.Data.Sqlite;

namespace erp_inteligente.Core.Planner;

/// <summary>Result of running one tool: the JSON for the model and whether the call was accepted.</summary>
/// <param name="Raw">The text is an approved instruction (a skill) sent as is, not wrapped as reference data.</param>
internal sealed record ToolOutcome(string Json, bool Allowed, string Code, bool Raw = false);

/// <summary>
/// The read tools of the Planner. Arguments are validated by JSON Schema (and a strict regex) before they touch the
/// database; every query is parameterized, runs on the AI read-only connection and is scoped to the session's warehouse.
/// </summary>
internal static partial class PlannerTools
{
    internal const int MaxRows = 50;
    internal const int MaxResultBytes = 8 * 1024;

    internal const string GetProduct = "get_product";
    internal const string GetStock = "get_stock";
    internal const string LoadSkill = "load_skill";

    internal static readonly string[] ReadToolNames =
        [SearchProducts, GetProduct, GetStock, SearchLocations, SearchZones, SearchLots, SearchOrders, SearchPartners];

    /// <summary>The search tools that share the free-text and paging arguments (checked by <see cref="InvalidSharedFields"/>).</summary>
    private static readonly HashSet<string> SharedSearchTools =
        new([SearchLocations, SearchZones, SearchLots, SearchOrders, SearchPartners], StringComparer.Ordinal);

    private const string SkuRx = "^[A-Z]{3}-[0-9]{3}$";
    private const string LocationRx = "^[A-Z]{1,4}-[0-9]{2}(-[0-9]{2})?$";

    // \z instead of $: the JSON Schema "$" would accept a trailing newline.
    [GeneratedRegex(@"^[A-Z]{3}-[0-9]{3}\z")] private static partial Regex Sku();
    [GeneratedRegex(@"^[A-Z]{1,4}-[0-9]{2}(-[0-9]{2})?\z")] private static partial Regex Location();
    [GeneratedRegex(@"^[A-Z]{1,4}\z")] private static partial Regex Zone();

    private static readonly EvaluationOptions EvalOptions = new() { OutputFormat = OutputFormat.List };

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        WriteIndented = false,
    };

    private static readonly Dictionary<string, (string Description, string Schema)> Definitions = new(StringComparer.Ordinal)
    {
        [SearchProducts] = (SearchProductsDescription, SearchProductsSchema),
        [GetProduct] = ("Dados de um produto pelo SKU exato. Sem o SKU exato, use search_products.",
            ObjectSchema(("sku", $$"""{"type":"string","pattern":"{{SkuRx}}","description":"SKU, formato ABC-123"}"""), required: ["sku"])),
        [GetStock] = ("Saldo de estoque do armazém por SKU e/ou endereço (ao menos um dos dois).",
            ObjectSchema(
                ("sku", $$"""{"type":"string","pattern":"{{SkuRx}}"}"""),
                ("location", $$"""{"type":"string","pattern":"{{LocationRx}}","description":"Endereço, ex.: A-01-01 ou DOCA-01"}"""))),
        // Not in ReadToolNames: offered by the Planner only when it has a skill catalog, and run by it (not by Execute).
        [LoadSkill] = ("Carrega o texto de uma skill (procedimento aprovado) pelo nome do índice do prompt. No máximo 3 por pedido.",
            ObjectSchema(("nome", """{"type":"string","pattern":"^[a-z][a-z_]{1,39}$","description":"Nome da skill, como no índice"}"""), required: ["nome"])),
        // Not in ReadToolNames: offered only when the Planner has the retrievers / the right (run by the Planner, not by Execute).
        [SearchKnowledge] = (SearchKnowledgeDescription, SearchKnowledgeSchema),
        [SearchMemories] = (SearchMemoriesDescription, SearchMemoriesSchema),
        [Remember] = (RememberDescription, RememberSchema),
        [SearchLocations] = (SearchLocationsDescription, SearchLocationsSchema),
        [SearchZones] = (SearchZonesDescription, SearchZonesSchema),
        [SearchLots] = (SearchLotsDescription, SearchLotsSchema),
        [SearchOrders] = (SearchOrdersDescription, SearchOrdersSchema),
        [SearchPartners] = (SearchPartnersDescription, SearchPartnersSchema),
    };

    private static readonly Dictionary<string, JsonSchema> Compiled =
        Definitions.ToDictionary(d => d.Key, d => JsonSchema.FromText(d.Value.Schema), StringComparer.Ordinal);

    internal static IEnumerable<LlmTool> ReadTools() =>
        ReadToolNames.Select(n => new LlmTool(n, Definitions[n].Description, Definitions[n].Schema));

    internal static LlmTool DefinitionTool(string name) => new(name, Definitions[name].Description, Definitions[name].Schema);

    internal static LlmTool LoadSkillTool() => new(LoadSkill, Definitions[LoadSkill].Description, Definitions[LoadSkill].Schema);

    internal static bool IsReadTool(string name) => Definitions.ContainsKey(name);

    /// <summary>Status line (Portuguese) for a read tool; arguments are already validated.</summary>
    internal static string StatusFor(string tool, JsonElement args)
    {
        string? Get(string key) => args.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
        return tool switch
        {
            LoadSkill => $"Lendo a skill {Get("nome")}",
            SearchKnowledge => "Consultando procedimentos",
            SearchMemories => "Consultando memórias",
            Remember => "Registrando memória",
            SearchProducts => SearchStatus(args),
            GetProduct => $"Consultando o produto {Get("sku")}",
            GetStock when Get("sku") is { } sku => $"Consultando saldo do {sku}",
            GetStock => $"Consultando o endereço {Get("location")}",
            SearchLocations => ArgText(args, "zone") is { } zone ? $"Pesquisando endereços da zona {zone.ToUpperInvariant()}" : SearchStatusFor("endereços", args),
            SearchZones => SearchStatusFor("zonas", args),
            SearchLots => SearchStatusFor("lotes", args),
            SearchOrders => SearchStatusFor("pedidos", args),
            SearchPartners => SearchStatusFor("fornecedores e clientes", args),
            _ => "Consultando",
        };
    }

    /// <summary>Parses and validates the arguments of a read tool. Returns an error outcome, or null when valid.</summary>
    internal static ToolOutcome? Validate(string tool, string argumentsJson, out JsonElement args)
    {
        args = default;
        JsonElement parsed;
        try
        {
            using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(argumentsJson) ? "{}" : argumentsJson,
                new JsonDocumentOptions { MaxDepth = 8 });
            parsed = doc.RootElement.Clone();
        }
        catch (JsonException)
        {
            return Error("invalid_arguments", "Os argumentos não são um JSON válido.");
        }
        if (parsed.ValueKind != JsonValueKind.Object)
            return Error("invalid_arguments", "Os argumentos devem ser um objeto JSON.");

        var results = Compiled[tool].Evaluate(parsed, EvalOptions);
        var bad = new SortedSet<string>(StringComparer.Ordinal);
        if (!results.IsValid)
        {
            foreach (var detail in results.Details ?? [])
                if (!detail.IsValid)
                    bad.Add(detail.InstanceLocation.ToString().TrimStart('/'));
        }
        // Defense in depth: the strict regexes reject what the JSON Schema "$" would let through (trailing newline).
        foreach (var (key, rx) in new[] { ("sku", Sku()), ("location", Location()), ("zone", Zone()) })
            if (tool is GetProduct or GetStock && parsed.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String && !rx.IsMatch(v.GetString()!))
                bad.Add(key);

        if (tool == LoadSkill && parsed.TryGetProperty("nome", out var skillName) && skillName.ValueKind == JsonValueKind.String
            && !SkillDocument.IsValidName(skillName.GetString()))
            bad.Add("nome");

        if (tool == SearchProducts && results.IsValid)
            bad.UnionWith(InvalidSearchFields(parsed));
        if (SharedSearchTools.Contains(tool) && results.IsValid)
            bad.UnionWith(InvalidSharedFields(tool, parsed));

        if (tool == GetStock && !parsed.TryGetProperty("sku", out _) && !parsed.TryGetProperty("location", out _))
            return Error("invalid_arguments", "Informe sku, location ou os dois.");
        if (!results.IsValid || bad.Count > 0)
        {
            var fields = bad.Where(f => f.Length is > 0 and <= 20 && f.All(c => char.IsAsciiLetterOrDigit(c) || c == '_')).ToArray();
            return Error("invalid_arguments", fields.Length > 0
                ? "Argumentos inválidos: " + string.Join(", ", fields) + ". Siga o schema da ferramenta."
                : "Argumentos inválidos. Siga o schema da ferramenta.");
        }
        args = parsed;
        return null;
    }

    /// <summary>Runs an already validated read tool.</summary>
    internal static ToolOutcome Execute(NexusDatabase database, string tool, JsonElement args, long warehouseId)
    {
        switch (tool)
        {
            case SearchProducts: return ExecuteSearchProducts(database, args, warehouseId);
            case SearchLocations: return ExecuteSearchLocations(database, args, warehouseId);
            case SearchZones: return ExecuteSearchZones(database, args, warehouseId);
            case SearchLots: return ExecuteSearchLots(database, args, warehouseId);
            case SearchOrders: return ExecuteSearchOrders(database, args, warehouseId);
            case SearchPartners: return ExecuteSearchPartners(database, args);
        }

        using var connection = database.OpenAiReadOnly();
        using var command = connection.CreateCommand();
        string? Get(string key) => args.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
        command.Parameters.AddWithValue("$warehouse_id", warehouseId);

        switch (tool)
        {
            case GetProduct:
                command.CommandText =
                    "SELECT sku, name, description, unit, weight_kg, tracks_expiry FROM v_ai_products WHERE sku = $sku LIMIT 1";
                command.Parameters.AddWithValue("$sku", Get("sku")!);
                break;
            case GetStock:
                command.CommandText =
                    "SELECT sku, product_name, location_code, zone_code, lot_code, expires_on, quantity, reserved_qty, available_qty " +
                    "FROM v_ai_stock WHERE warehouse_id = $warehouse_id AND ($sku IS NULL OR sku = $sku) " +
                    "AND ($location IS NULL OR location_code = $location) AND quantity <> 0 " +
                    "ORDER BY sku, location_code, lot_code LIMIT $limit";
                command.Parameters.AddWithValue("$sku", (object?)Get("sku") ?? DBNull.Value);
                command.Parameters.AddWithValue("$location", (object?)Get("location") ?? DBNull.Value);
                break;
            default:
                return Error("unknown_tool", "Ferramenta desconhecida.");
        }
        if (command.CommandText.Contains("$limit", StringComparison.Ordinal))
            command.Parameters.AddWithValue("$limit", MaxRows + 1);

        var rows = new List<JsonObject>();
        using (var reader = command.ExecuteReader())
        {
            while (reader.Read())
            {
                var row = new JsonObject();
                for (var i = 0; i < reader.FieldCount; i++)
                    row[reader.GetName(i)] = reader.IsDBNull(i) ? null : reader.GetValue(i) switch
                    {
                        long l => JsonValue.Create(l),
                        double d => JsonValue.Create(d),
                        var other => JsonValue.Create(Convert.ToString(other, CultureInfo.InvariantCulture)),
                    };
                rows.Add(row);
            }
        }

        return Rows(rows);
    }

    /// <summary>Packs rows as compact JSON: at most <see cref="MaxRows"/> rows and <see cref="MaxResultBytes"/> bytes.</summary>
    internal static ToolOutcome Rows(List<JsonObject> rows)
    {
        var truncated = rows.Count > MaxRows;
        if (truncated) rows = rows.Take(MaxRows).ToList();
        while (true)
        {
            var node = new JsonObject { ["count"] = rows.Count, ["rows"] = new JsonArray(rows.Select(r => (JsonNode?)r.DeepClone()).ToArray()) };
            if (truncated) node["truncated"] = true;
            var text = Serialize(node);
            if (System.Text.Encoding.UTF8.GetByteCount(text) <= MaxResultBytes || rows.Count == 0)
                return new ToolOutcome(text, true, "ok");
            rows = rows.Take(rows.Count / 2).ToList();
            truncated = true;
        }
    }

    internal static ToolOutcome Error(string code, string message) =>
        new(Serialize(new JsonObject { ["error"] = code, ["message"] = message }), false, code);

    /// <summary>Compact JSON in which '&lt;' and '&gt;' are escaped, so data can never close the surrounding data block.</summary>
    internal static string Serialize(JsonNode node) =>
        node.ToJsonString(JsonOptions).Replace("<", "\\u003c", StringComparison.Ordinal).Replace(">", "\\u003e", StringComparison.Ordinal);

    private static string ObjectSchema(params (string Name, string Schema)[] properties) =>
        ObjectSchema(properties, []);

    private static string ObjectSchema((string Name, string Schema) property, string[] required) =>
        ObjectSchema([property], required);

    private static string ObjectSchema((string Name, string Schema)[] properties, string[] required)
    {
        var props = new JsonObject();
        foreach (var (name, schema) in properties) props[name] = JsonNode.Parse(schema);
        var root = new JsonObject { ["type"] = "object", ["properties"] = props, ["additionalProperties"] = false };
        if (required.Length > 0) root["required"] = new JsonArray(required.Select(r => (JsonNode?)JsonValue.Create(r)).ToArray());
        return root.ToJsonString();
    }
}
