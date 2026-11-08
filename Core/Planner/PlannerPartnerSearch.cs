using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Nodes;
using erp_inteligente.Core.Data;

namespace erp_inteligente.Core.Planner;

/// <summary>
/// <c>search_partners</c>: suppliers and customers by partial name, kind and active flag. Documents, addresses and
/// contacts are not exposed (the <c>v_ai_partners</c> view has only kind, id, name and is_active). Partners are
/// company-wide master data, not scoped to a warehouse.
/// </summary>
internal static partial class PlannerTools
{
    internal const string SearchPartners = "search_partners";

    private const string SearchPartnersDescription =
        "Pesquisa fornecedores e clientes por parte do nome (sem acento ou maiúsculas), tipo e ativo. Devolve kind " +
        "(supplier ou customer), name e is_active. Use antes de criar um fornecedor ou cliente, para ver se já existe.";

    private const string SearchPartnersSchema = """
        {"type":"object","additionalProperties":false,"properties":{
          "kind":{"type":"string","enum":["supplier","customer"],"description":"supplier: fornecedor; customer: cliente. Sem o argumento, ambos."},
          "query":{"type":"string","minLength":1,"maxLength":100,"description":"Parte do nome."},
          "active":{"type":"boolean","description":"true: só ativos; false: só inativos."},
          "limit":{"type":"integer","minimum":1,"maximum":50,"description":"Itens por página (padrão 20)."},
          "offset":{"type":"integer","minimum":0,"maximum":10000,"description":"Quantos itens pular (paginação)."}
        }}
        """;

    private static ToolOutcome ExecuteSearchPartners(NexusDatabase database, JsonElement args)
    {
        var terms = SearchTerms(ArgText(args, "query"));
        var kind = ArgText(args, "kind");
        var (limit, offset) = ArgPage(args);

        using var connection = database.OpenAiReadOnly();
        connection.CreateFunction("nexus_fold", (string? value) => Fold(value), isDeterministic: true);

        var where = new List<string>();
        var parameters = new List<(string, object)>();
        AddTermConditions(terms, "p.name", where, parameters);
        if (kind is not null)
        {
            where.Add("p.kind = $kind");
            parameters.Add(("$kind", kind));
        }
        if (ArgFlag(args, "active") is { } active)
        {
            where.Add("p.is_active = $active");
            parameters.Add(("$active", active ? 1 : 0));
        }
        const string from = "FROM v_ai_partners p ";
        var filter = where.Count == 0 ? "" : "WHERE " + string.Join(" AND ", where) + " ";

        long total;
        using (var count = connection.CreateCommand())
        {
            count.CommandText = "SELECT COUNT(*) " + from + filter;
            Bind(count, parameters);
            total = (long)count.ExecuteScalar()!;
        }

        using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT p.kind, p.name, p.is_active " + from + filter +
            "ORDER BY CASE WHEN $first IS NOT NULL AND nexus_fold(p.name) LIKE $first ESCAPE '\\' THEN 0 ELSE 1 END, " +
            "p.name, p.kind LIMIT $limit OFFSET $offset";
        Bind(command, parameters);
        command.Parameters.AddWithValue("$first", terms.Count == 0 ? DBNull.Value : LikeLiteral(terms[0]) + "%");
        command.Parameters.AddWithValue("$limit", limit);
        command.Parameters.AddWithValue("$offset", offset);

        var items = new List<JsonObject>();
        using (var reader = command.ExecuteReader())
        {
            while (reader.Read())
            {
                items.Add(new JsonObject
                {
                    ["kind"] = reader.GetString(0),
                    ["name"] = reader.GetString(1),
                    ["is_active"] = reader.GetInt64(2) == 1,
                });
            }
        }
        return SearchPage(items, total, offset, "Nenhum fornecedor ou cliente encontrado. Tente parte do nome; se não existir, um plano pode cadastrá-lo (create_supplier, create_customer).");
    }
}
