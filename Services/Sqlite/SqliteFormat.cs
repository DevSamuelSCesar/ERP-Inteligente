using System;
using System.Collections.Frozen;
using System.Collections.Generic;
using System.Globalization;
using Microsoft.Data.Sqlite;

namespace erp_inteligente.Services.Sqlite;

/// <summary>Conversions from database text to display text: Portuguese labels, UTC instants and ISO dates.</summary>
internal static class SqliteFormat
{
    private const string IsoDate = "yyyy-MM-dd";

    private static readonly FrozenDictionary<string, string> AuditActions = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["auth.login"] = "Login",
        ["auth.login_failed"] = "Falha de login",
        ["auth.locked"] = "Usuário bloqueado",
        ["auth.login_locked"] = "Login recusado (bloqueio)",
        ["auth.logout"] = "Saída",
        ["auth.session_expired"] = "Sessão expirada",
        ["policy.denied"] = "Acesso negado",
        ["ai.query"] = "Consulta do Nexus",
        ["plan.submit"] = "Plano enviado",
        ["plan.execute"] = "Plano executado",
        ["plan.approve"] = "Plano aprovado",
        ["plan.reject"] = "Plano rejeitado",
        ["plan.failed"] = "Plano falhou",
        ["stock.adjust"] = "Ajuste de estoque",
        ["ai.tool"] = "Ferramenta do Nexus",
        ["ai.context"] = "Contexto do Nexus",
        ["ai.injection"] = "Instrução suspeita",
        ["ai.recipe_match"] = "Reconhecimento de receita",
        ["memory.create"] = "Memória criada",
        ["memory.update"] = "Memória corrigida",
        ["memory.archive"] = "Memória arquivada",
        ["memory.reactivate"] = "Memória reativada",
        ["memory.reinforce"] = "Memória reforçada",
        ["memory.maintenance"] = "Manutenção de memórias",
        ["skill.create"] = "Skill criada",
        ["skill.update"] = "Skill alterada",
        ["skill.archive"] = "Skill arquivada",
        ["skill.reactivate"] = "Skill reativada",
        ["recipe.candidate"] = "Receita candidata",
        ["recipe.promote"] = "Receita promovida",
        ["recipe.suspend"] = "Receita suspensa",
        ["recipe.reactivate"] = "Receita reativada",
        ["recipe.demote"] = "Receita despromovida",
        ["llm.key.set"] = "Chave de IA salva",
        ["llm.key.clear"] = "Chave de IA removida",
        ["llm.model.set"] = "Modelo de IA alterado",
        ["llm.local.port"] = "Porta do modelo local",
        ["backup.create"] = "Backup",
        ["backup.restore"] = "Restauração de backup",
    }.ToFrozenDictionary(StringComparer.Ordinal);

    /// <summary>The audit panel's categories, in display order.</summary>
    internal static readonly string[] AuditCategories = ["Planos", "IA", "Segurança", "Conhecimento", "Sistema"];

    /// <summary>Category of an <c>audit_log.action</c> (by its prefix).</summary>
    internal static string AuditCategory(string action) => action.Split('.')[0] switch
    {
        "plan" or "stock" => "Planos",
        "ai" => "IA",
        "auth" or "policy" or "user" or "role" => "Segurança",
        "memory" or "skill" or "recipe" => "Conhecimento",
        _ => "Sistema",
    };

    /// <summary>Parses a UTC timestamp such as <c>2026-10-20T10:01:00Z</c>.</summary>
    internal static DateTimeOffset ParseUtc(string text) =>
        DateTimeOffset.Parse(text, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal);

    /// <summary>Parses an ISO date (<c>YYYY-MM-DD</c>) of a non-null column.</summary>
    internal static DateOnly ParseDate(string text) => DateOnly.ParseExact(text, IsoDate, CultureInfo.InvariantCulture);

    /// <summary>Reads a nullable ISO date column; NULL becomes null.</summary>
    internal static DateOnly? ReadOptionalDate(SqliteDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal) ? null : ParseDate(reader.GetString(ordinal));

    /// <summary>Reads a nullable ISO date column for a display type that cannot hold null; NULL becomes <see cref="DateOnly.MinValue"/>.</summary>
    internal static DateOnly ReadDateOrMinValue(SqliteDataReader reader, int ordinal) =>
        ReadOptionalDate(reader, ordinal) ?? DateOnly.MinValue;

    /// <summary>Portuguese label of <c>inbound_orders.status</c> and <c>outbound_orders.status</c>; unknown values are shown raw.</summary>
    internal static string OrderStatusLabel(string status) => status switch
    {
        "received" => "Recebido",
        "expected" => "Previsto",
        "receiving" => "Em recebimento",
        "reserved" => "Reservado",
        "open" => "Aberto",
        "picking" => "Em separação",
        "shipped" => "Expedido",
        "cancelled" => "Cancelado",
        _ => status,
    };

    /// <summary>Portuguese label of <c>recipes.status</c>; unknown values are shown raw.</summary>
    internal static string RecipeStatusLabel(string status) => status switch
    {
        "candidate" => "Candidata",
        "active" => "Ativa",
        "suspended" => "Suspensa",
        "demoted" => "Despromovida",
        _ => status,
    };

    /// <summary>Portuguese label of <c>audit_log.action</c>; unknown actions are shown raw.</summary>
    internal static string AuditActionLabel(string action) =>
        AuditActions.TryGetValue(action, out var label) ? label : action;
}
