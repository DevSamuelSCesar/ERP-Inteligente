using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json.Nodes;
using erp_inteligente.Core.Engine;

namespace erp_inteligente.ViewModels;

/// <summary>Where a form field takes its suggestions from (references/UI.md, section 5.2).</summary>
public enum PlanLookup
{
    None,
    Sku,
    Location,
    Zone,
    Supplier,
    Customer,
    InboundOrder,
    OutboundOrder,
    Unit,
}

/// <summary>One input of an operation form, derived from the operation's JSON Schema.</summary>
public sealed record PlanFieldSpec(string Key, string Label, PlanFieldKind Kind, bool Required, string Hint, PlanLookup Lookup);

/// <summary>
/// Builds the "Novo plano" forms from <see cref="OperationCatalog"/>: every operation of the catalog gets a form, so
/// whatever the Nexus can propose the operator can also do by hand. Labels come from the same table the validator uses.
/// </summary>
public static class PlanForms
{
    /// <summary>Text fields whose values are codes and are upper-cased before validation.</summary>
    public static readonly IReadOnlySet<string> CodeFields = new HashSet<string>(StringComparer.Ordinal)
    {
        "sku", "location", "from", "to", "code", "zone", "document_number", "order_number", "inbound_order", "lot",
    };

    private static readonly Dictionary<string, string> Hints = new(StringComparer.Ordinal)
    {
        ["sku"] = "ABC-123", ["qty"] = "10", ["location"] = "A-02-01", ["from"] = "DOCA-01", ["to"] = "A-02-01",
        ["zone"] = "A", ["capacity"] = "60", ["unit"] = "cx", ["reason"] = "Avaria na embalagem", ["aisle"] = "1",
        ["level_from"] = "1", ["level_to"] = "5", ["document_masked"] = "**.***.***/0001-**",
        ["document_number"] = "NF-FICT-000103", ["order_number"] = "PED-FICT-5003", ["inbound_order"] = "NF-FICT-000102",
        ["expires_on"] = "AAAA-MM-DD", ["expected_on"] = "AAAA-MM-DD", ["due_on"] = "AAAA-MM-DD",
        ["lines"] = "ABC-123 10 (um item por linha)", ["weight_kg"] = "2,5", ["unit_cost"] = "12,90",
    };

    /// <summary>The business skill a plan made of this operation belongs to (shown on the plan board).</summary>
    public static string SkillOf(string op) => op switch
    {
        "receive_goods" or "create_inbound_order" or "cancel_inbound_order" => "recebimento",
        "move_stock" => "armazenagem",
        "adjust_stock" => "inventario",
        "pick_items" => "separacao",
        "create_outbound_order" or "cancel_outbound_order" or "reserve_stock" or "release_reservation" or "ship_order" => "expedicao",
        _ => "cadastros",
    };

    public static IReadOnlyList<PlanOperationOption> Build(IEnumerable<OperationDefinition> operations) =>
        operations.Select(o => new PlanOperationOption(o.Name, o.Label, o.Permission, o.Name, SkillOf(o.Name),
            FieldsOf(o.Name))).ToList();

    public static IReadOnlyList<PlanFieldSpec> FieldsOf(string op)
    {
        var schema = JsonNode.Parse(OperationCatalog.SchemaJson(op))!.AsObject();
        var required = (schema["required"] as JsonArray)?.Select(n => n!.GetValue<string>()).ToHashSet(StringComparer.Ordinal)
                       ?? [];
        var fields = new List<PlanFieldSpec>();
        foreach (var (key, node) in schema["properties"]!.AsObject())
        {
            var kind = KindOf(key, node!.AsObject());
            var label = Capitalize(SchemaValidator.LabelOf(key) is var known && known != "um campo" ? known : key);
            if (op == "adjust_stock" && key == "qty")
                label = "Quantidade (negativa para baixa)";
            var hint = op is "create_zone" or "update_zone" && key == "code" ? "Q"
                : op is "create_location" or "update_location" && key == "code" ? "A-03-01"
                : Hints.GetValueOrDefault(key, "");
            fields.Add(new PlanFieldSpec(key, label, kind, required.Contains(key), hint, LookupOf(op, key)));
        }
        // Required fields first, keeping the schema order inside each group.
        return fields.OrderBy(f => f.Required ? 0 : 1).ToList();
    }

    private static PlanFieldKind KindOf(string key, JsonObject node)
    {
        var type = node["type"]?.GetValue<string>()
                   ?? (node["anyOf"] as JsonArray)?.Select(n => n?["type"]?.GetValue<string>()).FirstOrDefault(t => t is not null);
        return type switch
        {
            "integer" => PlanFieldKind.Integer,
            "number" => PlanFieldKind.Decimal,
            "boolean" => PlanFieldKind.Flag,
            "array" => PlanFieldKind.Lines,
            _ when key.EndsWith("_on", StringComparison.Ordinal) => PlanFieldKind.Date,
            _ => PlanFieldKind.Text,
        };
    }

    private static PlanLookup LookupOf(string op, string key) => key switch
    {
        "sku" when op is "create_product" => PlanLookup.None,
        "sku" => PlanLookup.Sku,
        "location" or "from" or "to" => PlanLookup.Location,
        "code" when op is "update_location" => PlanLookup.Location,
        "code" when op is "update_zone" => PlanLookup.Zone,
        "zone" => PlanLookup.Zone,
        "unit" => PlanLookup.Unit,
        "supplier" => PlanLookup.Supplier,
        "customer" => PlanLookup.Customer,
        "inbound_order" => PlanLookup.InboundOrder,
        "document_number" when op is not "create_inbound_order" => PlanLookup.InboundOrder,
        "order_number" when op is not "create_outbound_order" => PlanLookup.OutboundOrder,
        _ => PlanLookup.None,
    };

    /// <summary>
    /// Parses order lines typed one per line as "SKU quantidade" (also "SKU: 10" or "SKU;10").
    /// Returns null and a Portuguese message when a line is malformed.
    /// </summary>
    public static List<Dictionary<string, object?>>? ParseLines(string text, out string? error)
    {
        error = null;
        var lines = new List<Dictionary<string, object?>>();
        var number = 0;
        foreach (var raw in text.Split('\n'))
        {
            number++;
            var line = raw.Trim();
            if (line.Length == 0) continue;
            var parts = line.Split([' ', '\t', ':', ';'], StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length != 2 || !int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var qty) || qty <= 0)
            {
                error = $"Itens, linha {number}: use \"SKU quantidade\", por exemplo ABC-123 10.";
                return null;
            }
            lines.Add(new Dictionary<string, object?> { ["sku"] = parts[0].ToUpperInvariant(), ["qty"] = qty });
        }
        if (lines.Count == 0)
            error = "Itens: informe ao menos um item, um por linha.";
        return lines.Count == 0 ? null : lines;
    }

    /// <summary>
    /// The first unused code after the existing ones of a zone: next level in the zone's highest aisle
    /// (Q-01-03 → Q-01-04), or ZONA-01-01 for an empty zone. Codes are unique in the whole database.
    /// </summary>
    public static string NextLocationCode(string zone, IEnumerable<string> existingCodes)
    {
        var codes = existingCodes.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var parsed = codes.Select(c => c.Split('-'))
            .Where(p => p.Length == 3 && p[0] == zone && int.TryParse(p[1], out _) && int.TryParse(p[2], out _))
            .Select(p => (Aisle: int.Parse(p[1], CultureInfo.InvariantCulture), Level: int.Parse(p[2], CultureInfo.InvariantCulture)))
            .ToList();
        var aisle = parsed.Count == 0 ? 1 : parsed.Max(p => p.Aisle);
        for (var a = aisle; a <= 99; a++)
        for (var level = 1; level <= 99; level++)
        {
            var code = $"{zone}-{a:00}-{level:00}";
            if (!codes.Contains(code) && (a > aisle || level > parsed.Where(p => p.Aisle == a).Select(p => p.Level).DefaultIfEmpty(0).Max()))
                return code;
        }
        return $"{zone}-01-01";
    }

    /// <summary>The first aisle of a zone with no location yet (1 for an empty zone).</summary>
    public static int NextFreeAisle(string zone, IEnumerable<string> existingCodes)
    {
        var used = existingCodes.Select(c => c.Split('-'))
            .Where(p => p.Length == 3 && p[0] == zone && int.TryParse(p[1], out _))
            .Select(p => int.Parse(p[1], CultureInfo.InvariantCulture))
            .ToHashSet();
        return Enumerable.Range(1, 99).FirstOrDefault(a => !used.Contains(a), 99);
    }

    private static string Capitalize(string text) =>
        text.Length == 0 ? text : char.ToUpper(text[0], CultureInfo.GetCultureInfo("pt-BR")) + text[1..];
}
