using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using erp_inteligente.Core.Audit;
using erp_inteligente.Core.Data;
using erp_inteligente.Core.Policy;
using erp_inteligente.Models;
using Microsoft.Data.Sqlite;

namespace erp_inteligente.Services.Sqlite;

/// <summary>
/// The Nexus while the planner does not exist (ROADMAP.md, feature 3): it answers stock questions and nothing else.
/// Reads go through the AI connection (<see cref="NexusDatabase.OpenAiReadOnly"/>), which the authorizer limits to
/// the <c>v_ai_*</c> views. The AI acts with the intersection of its role and the user's rights, and every read is audited.
/// </summary>
public sealed partial class ReadOnlyNexusService(NexusDatabase database, PolicyEngine policy, IAuditLog audit) : INexusService
{
    private const int MaxRequestLength = 2000;
    private const string Refused = "Seu papel não permite consultas pelo Nexus.";
    private const string ChangesUnavailable =
        "Alterar dados chega com o motor de planos (feature 2 do roadmap). Por enquanto o Nexus só consulta saldos.";
    private const string Guidance =
        "Não entendi o pedido. Por enquanto o Nexus só consulta saldos, por exemplo: \"saldo do ABC-123\".";

    private static readonly string[] ChangeWords = ["guard", "armazen", "mov", "transfer", "ajust", "avaria", "expedi", "separ"];
    private static readonly string[] StockWords = ["saldo", "quanto", "onde", "estoque"];

    public async Task<NexusReply> SendAsync(string request, UserSession user, IProgress<string> status, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(user);
        ArgumentNullException.ThrowIfNull(status);

        request = request.Trim();
        if (request.Length == 0)
            return new NexusReply("Escreva um pedido.", IsError: true);
        if (request.Length > MaxRequestLength)
            return new NexusReply($"O pedido passa de {MaxRequestLength} caracteres.", IsError: true);

        if (!policy.IsAllowed(user, Permissions.StockRead) || !policy.EffectiveForAi(user).Contains(Permissions.StockRead))
        {
            audit.Append(new AuditEntry(AuditActorType.Ai, null, user.UserId, "ai.query", "permission:" + Permissions.StockRead, AuditOutcome.Denied));
            return new NexusReply(Refused, IsError: true);
        }

        var upper = request.ToUpperInvariant();
        var words = RemoveAccents(request.ToLowerInvariant());
        var sku = SkuPattern().Match(upper) is { Success: true } match ? match.Value : null;

        if (ContainsAny(words, ChangeWords))
            return new NexusReply(ChangesUnavailable);

        if (sku is not null && ContainsAny(words, StockWords))
            return await DescribeStockAsync(sku, user, status, cancellationToken);

        return new NexusReply(Guidance);
    }

    private async Task<NexusReply> DescribeStockAsync(string sku, UserSession user, IProgress<string> status, CancellationToken cancellationToken)
    {
        status.Report($"Consultando saldo do {sku}");
        audit.Append(new AuditEntry(AuditActorType.Ai, null, user.UserId, "ai.query", "sku:" + sku, AuditOutcome.Allowed));

        await using var connection = database.OpenAiReadOnly();
        var product = await ReadProductAsync(connection, sku, cancellationToken);
        if (product is null)
            return new NexusReply($"SKU {sku} não existe.", IsError: true);

        var places = await ReadPlacesAsync(connection, sku, user.WarehouseId, cancellationToken);
        if (places.Count == 0)
            return new NexusReply($"{sku} ({product.Value.Name}) está sem saldo.");

        var described = places.Select(p => $"{p.Quantity} {product.Value.Unit} em {p.Location}");
        return new NexusReply($"{sku} ({product.Value.Name}): {string.Join("; ", described)}.");
    }

    /// <summary>Name and unit of a SKU from <c>v_ai_products</c>, or null when the SKU does not exist.</summary>
    private static async Task<(string Name, string Unit)?> ReadProductAsync(SqliteConnection connection, string sku, CancellationToken ct)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT name, unit FROM v_ai_products WHERE sku = $sku";
        command.Parameters.AddWithValue("$sku", sku);

        await using var reader = await command.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct)) return null;
        return (reader.GetString(0), reader.GetString(1));
    }

    /// <summary>Positive balances of the SKU in the user's warehouse, grouped by location, from <c>v_ai_stock</c>.</summary>
    private static async Task<List<(string Location, long Quantity)>> ReadPlacesAsync(SqliteConnection connection, string sku, long warehouseId, CancellationToken ct)
    {
        await using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT location_code, SUM(quantity) FROM v_ai_stock " +
            "WHERE sku = $sku AND warehouse_id = $warehouse_id AND quantity > 0 " +
            "GROUP BY location_code ORDER BY location_code";
        command.Parameters.AddWithValue("$sku", sku);
        command.Parameters.AddWithValue("$warehouse_id", warehouseId);

        var places = new List<(string Location, long Quantity)>();
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
            places.Add((reader.GetString(0), reader.GetInt64(1)));
        return places;
    }

    private static bool ContainsAny(string text, string[] keys) => keys.Any(text.Contains);

    private static string RemoveAccents(string text)
    {
        var builder = new StringBuilder(text.Length);
        foreach (var c in text.Normalize(NormalizationForm.FormD))
        {
            if (CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark)
                builder.Append(c);
        }
        return builder.ToString();
    }

    [GeneratedRegex(@"\b[A-Z]{3}-\d{3}\b")]
    private static partial Regex SkuPattern();
}
