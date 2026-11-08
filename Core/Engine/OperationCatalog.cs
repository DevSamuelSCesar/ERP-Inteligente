using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using erp_inteligente.Models;
using Json.Schema;

namespace erp_inteligente.Core.Engine;

/// <summary>Static description of one plan operation of the closed catalog.</summary>
/// <param name="Name">DSL name (e.g. <c>move_stock</c>).</param>
/// <param name="Risk">Risk level of the operation.</param>
/// <param name="Permission">Permission code the requesting user needs (<c>role_permissions</c>).</param>
/// <param name="UiOperation">The matching operation shown in the UI.</param>
/// <param name="Label">Portuguese label for the operator.</param>
/// <param name="ArgsSchema">JSON Schema of the arguments.</param>
/// <param name="ReferenceFields">Arguments that may hold a <c>$name</c> reference to an earlier step result.</param>
public sealed record OperationDefinition(
    string Name,
    RiskLevel Risk,
    string Permission,
    PlanOperation UiOperation,
    string Label,
    JsonSchema ArgsSchema,
    IReadOnlyList<string> ReferenceFields);

/// <summary>
/// The closed catalog of DSL v1 write operations. Schemas are the embedded files
/// <c>Core/Engine/Schemas/v1/&lt;op&gt;.schema.json</c>, also used as LLM tool schemas.
/// </summary>
public static class OperationCatalog
{
    private const string ResourcePrefix = "Nexus.Dsl.v1.";

    private static readonly Dictionary<string, string> SchemaTexts = new(StringComparer.Ordinal);
    private static readonly Dictionary<string, OperationDefinition> ByName = new(StringComparer.Ordinal);

    static OperationCatalog()
    {
        var list = new List<OperationDefinition>
        {
            Define("receive_goods", RiskLevel.Reversible, "stock.receive", PlanOperation.ReceiveGoods, "Recebimento", "sku", "location"),
            Define("move_stock", RiskLevel.Reversible, "stock.move", PlanOperation.MoveStock, "Movimentação", "sku", "from", "to"),
            Define("adjust_stock", RiskLevel.Critical, "stock.adjust", PlanOperation.AdjustStock, "Ajuste de estoque", "sku", "location"),
            Define("create_product", RiskLevel.Reversible, "catalog.manage", PlanOperation.CreateProduct, "Cadastro de produto"),
            Define("create_location", RiskLevel.Reversible, "location.manage", PlanOperation.CreateLocation, "Cadastro de endereço"),
        };
        foreach (var op in list)
            ByName.Add(op.Name, op);
        V1 = list.AsReadOnly();
    }

    /// <summary>All operations of DSL v1.</summary>
    public static IReadOnlyList<OperationDefinition> V1 { get; }

    /// <summary>Looks up an operation by its exact DSL name.</summary>
    public static bool TryGet(string name, out OperationDefinition definition)
    {
        if (name is not null && ByName.TryGetValue(name, out var found))
        {
            definition = found;
            return true;
        }
        definition = null!;
        return false;
    }

    /// <summary>The raw schema text of an operation, for the LLM tools of feature 3.</summary>
    public static string SchemaJson(string name)
    {
        if (name is null || !SchemaTexts.TryGetValue(name, out var text))
            throw new ArgumentException("Unknown operation.", nameof(name));
        return text;
    }

    private static OperationDefinition Define(
        string name, RiskLevel risk, string permission, PlanOperation ui, string label, params string[] references)
    {
        var text = LoadSchemaText(name);
        SchemaTexts[name] = text;
        return new OperationDefinition(name, risk, permission, ui, label, JsonSchema.FromText(text), references);
    }

    private static string LoadSchemaText(string name)
    {
        var resource = $"{ResourcePrefix}{name}.schema.json";
        using var stream = typeof(OperationCatalog).Assembly.GetManifestResourceStream(resource)
            ?? throw new InvalidOperationException($"Embedded schema not found: {resource}");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}
