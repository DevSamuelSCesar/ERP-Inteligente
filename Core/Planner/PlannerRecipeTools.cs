using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using erp_inteligente.Core.Data;

namespace erp_inteligente.Core.Planner;

/// <summary>
/// <c>search_recipes</c> (feature 7): active recipes read through <c>v_ai_recipes</c> on the AI read-only connection, so
/// the model can base a plan on one that already worked. A recipe is a template: the model fills it in a normal
/// <c>propose_plan</c>, validated like any other.
/// </summary>
internal static partial class PlannerTools
{
    internal const string SearchRecipes = "search_recipes";
    private const int MaxRecipeLimit = 10;

    private const string SearchRecipesDescription =
        "Busca receitas ativas (planos que já funcionaram, aprovados pelo supervisor). Aceita parte do nome ou da descrição, sem acento. " +
        "Devolve name, description, skill, template (passos com $parametro) e parameters. Use uma receita como base do plano: " +
        "troque cada $parametro pelo valor e chame propose_plan.";

    private const string SearchRecipesSchema = """
        {"type":"object","additionalProperties":false,"properties":{
          "query":{"type":"string","minLength":1,"maxLength":100,"description":"Parte do nome ou da descrição (ex.: doca, armazenar)."},
          "limit":{"type":"integer","minimum":1,"maximum":10,"description":"Receitas devolvidas (padrão 5)."}
        }}
        """;

    internal static ToolOutcome ExecuteSearchRecipes(NexusDatabase database, JsonElement args)
    {
        var limit = System.Math.Clamp(Number(args, "limit") ?? 5, 1, MaxRecipeLimit);
        var terms = Text(args, "query") is { } query ? SearchTerms(query) : [];
        var where = new List<string>();
        var parameters = new List<(string, object)>();
        AddTermConditions(terms, "name || ' ' || description", where, parameters);

        using var connection = database.OpenAiReadOnly();
        connection.CreateFunction("nexus_fold", (string? value) => Fold(value), isDeterministic: true);
        using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT name, description, intent, skill, plan_template_json, params_schema_json FROM v_ai_recipes" +
            (where.Count > 0 ? " WHERE " + string.Join(" AND ", where) : "") + " ORDER BY name LIMIT $limit";
        Bind(command, parameters);
        command.Parameters.AddWithValue("$limit", limit);

        var rows = new List<JsonObject>();
        using (var reader = command.ExecuteReader())
            while (reader.Read())
            {
                var template = JsonNode.Parse(reader.GetString(4));
                var schema = JsonNode.Parse(reader.GetString(5));
                rows.Add(new JsonObject
                {
                    ["name"] = reader.GetString(0),
                    ["description"] = reader.GetString(1),
                    ["intent"] = reader.GetString(2),
                    ["skill"] = reader.IsDBNull(3) ? null : reader.GetString(3),
                    ["template"] = template?["steps"]?.DeepClone(),
                    ["parameters"] = schema?["properties"] is JsonObject props
                        ? new JsonObject(props.Select(p => new KeyValuePair<string, JsonNode?>(p.Key, p.Value?["type"]?.DeepClone())))
                        : null,
                });
            }
        return Rows(rows);
    }
}
