using System;
using System.Collections.Generic;
using System.Text.Json.Nodes;
using static erp_inteligente.Core.Engine.StockStore;

namespace erp_inteligente.Core.Engine;

/// <summary>
/// Shared logic of create_supplier and create_customer: a name unique among the table's rows (case-insensitive,
/// accents included, compared in C# because SQLite folds only ASCII) and an optional already-masked document.
/// Table and column names are constants of the subclasses, never input.
/// </summary>
internal abstract class CreatePartnerHandler : IOperationHandler
{
    private readonly string _table;
    private readonly string _documentColumn;
    private readonly string _kind;
    private readonly string _noun;

    protected CreatePartnerHandler(string op, string table, string documentColumn, string kind, string noun)
    {
        Op = op;
        _table = table;
        _documentColumn = documentColumn;
        _kind = kind;
        _noun = noun;
    }

    public string Op { get; }

    public IReadOnlyList<PlanError> Validate(EngineContext ctx, PlanStepDocument step, JsonObject args)
    {
        var name = Str(args, "name")?.Trim();
        if (string.IsNullOrEmpty(name))
            return [Error(step, "name", "business.invalid_name", $"informe o nome do {_noun}.")];
        if (NameExists(ctx, name))
            return [Error(step, "name", "business.duplicate_name", $"já existe um {_noun} chamado {name}.")];
        return [];
    }

    public string Apply(EngineContext ctx, PlanStepDocument step, JsonObject args)
    {
        var name = (Str(args, "name") ?? throw new InvalidOperationException("Missing name.")).Trim();
        var document = Str(args, "document_masked");
        using var insert = ctx.Command($"INSERT INTO {_table} (name, {_documentColumn}) VALUES ($name, $doc)");
        insert.Parameters.AddWithValue("$name", name);
        insert.Parameters.AddWithValue("$doc", (object?)document ?? DBNull.Value);
        insert.ExecuteNonQuery();
        ctx.Diff.AddCreated(_kind, document ?? "—", name);
        return name;
    }

    private bool NameExists(EngineContext ctx, string name)
    {
        using var cmd = ctx.Command($"SELECT name FROM {_table}");
        using var r = cmd.ExecuteReader();
        while (r.Read())
            if (string.Equals(r.GetString(0).Trim(), name, StringComparison.OrdinalIgnoreCase))
                return true;
        return false;
    }
}

/// <summary>create_supplier: adds a supplier to the catalog.</summary>
internal sealed class CreateSupplierHandler()
    : CreatePartnerHandler("create_supplier", "suppliers", "cnpj_masked", "Fornecedor", "fornecedor");

/// <summary>create_customer: adds a customer to the catalog.</summary>
internal sealed class CreateCustomerHandler()
    : CreatePartnerHandler("create_customer", "customers", "document_masked", "Cliente", "cliente");
