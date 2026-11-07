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
        ["stock.adjust"] = "Ajuste de estoque",
    }.ToFrozenDictionary(StringComparer.Ordinal);

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
