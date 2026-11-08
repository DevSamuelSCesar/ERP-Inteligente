using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using erp_inteligente.Models;
using erp_inteligente.Services;

namespace Nexus.Tests.Fakes;

/// <summary>
/// Keyword-based stand-in for the LLM Planner (ROADMAP.md, feature 3), so the Nexus screen can be
/// exercised without any provider. It understands stock queries, putaway moves and stock adjustments.
/// </summary>
public sealed partial class DemoNexusService(DemoData data, TimeSpan stepDelay) : INexusService
{
    private const int MaxRequestLength = 2000;

    public async Task<NexusReply> SendAsync(string request, UserSession user, IProgress<string> status, CancellationToken cancellationToken = default)
    {
        request = request.Trim();
        if (request.Length == 0)
            return new NexusReply("Escreva um pedido.", IsError: true);
        if (request.Length > MaxRequestLength)
            return new NexusReply($"O pedido passa de {MaxRequestLength} caracteres.", IsError: true);
        if (!user.Has(Permissions.PlanSubmit))
            return new NexusReply("Seu papel não permite enviar pedidos ao Nexus.", IsError: true);

        var upper = request.ToUpperInvariant();
        var words = RemoveAccents(request.ToLowerInvariant());
        var sku = SkuPattern().Match(upper) is { Success: true } s ? s.Value : null;
        var locations = LocationPattern().Matches(upper).Select(m => m.Value).ToList();

        if (sku is not null)
        {
            status.Report($"Consultando saldo do {sku}");
            await Task.Delay(stepDelay, cancellationToken);
        }

        if (ContainsAny(words, "ajust", "avaria", "perda", "sobra"))
            return await PlanAdjustAsync(request, upper, words, sku, locations, user, status, cancellationToken);
        if (ContainsAny(words, "guard", "armazen", "mov", "transfir", "transfer"))
            return await PlanMoveAsync(upper, words, sku, locations, user, status, cancellationToken);
        if (sku is not null && ContainsAny(words, "saldo", "quanto", "onde", "estoque"))
            return DescribeStock(sku);

        return new NexusReply(
            "Não entendi o pedido. Posso consultar saldo (\"saldo do ABC-123\"), mover estoque " +
            "(\"guarde as 40 caixas do ABC-123 que estão na doca\") e ajustar estoque com motivo " +
            "(\"ajuste -1 cx do ALI-300 em C-01-01 por avaria\").");
    }

    private NexusReply DescribeStock(string sku)
    {
        var product = data.FindProduct(sku);
        if (product is null)
            return new NexusReply($"SKU {sku} não existe.", IsError: true);

        var places = data.Balances.Where(b => b.Sku == sku && b.Quantity > 0)
            .GroupBy(b => b.Location)
            .Select(g => $"{g.Sum(b => b.Quantity)} {product.Unit} em {g.Key}")
            .ToList();
        return places.Count == 0
            ? new NexusReply($"{sku} ({product.Name}) está sem saldo.")
            : new NexusReply($"{sku} ({product.Name}): {string.Join("; ", places)}.");
    }

    private async Task<NexusReply> PlanMoveAsync(string upper, string words, string? sku, List<string> locations,
        UserSession user, IProgress<string> status, CancellationToken cancellationToken)
    {
        if (sku is null || data.FindProduct(sku) is not { } product)
            return new NexusReply("Informe o SKU a mover (ex.: ABC-123).", IsError: true);

        var from = locations.FirstOrDefault() ?? (words.Contains("doca") ? "DOCA-01" : null);
        if (from is null)
            return new NexusReply("Informe de onde sai o estoque (ex.: DOCA-01).", IsError: true);

        var available = data.QuantityAt(sku, from);
        var quantity = FirstQuantity(upper) ?? available;
        if (quantity <= 0)
            return new NexusReply($"{from} não tem saldo de {sku}.", IsError: true);

        status.Report("Planejando");
        await Task.Delay(stepDelay, cancellationToken);

        var steps = locations.Count > 1
            ? [new PlanStep(PlanOperation.MoveStock, quantity, product.Unit, sku, from, locations[1])]
            : Allocate(sku, product.Unit, from, quantity);
        if (steps.Count == 0)
            return new NexusReply($"Não há endereço livre na zona A para {quantity} {product.Unit} de {sku}.", IsError: true);

        status.Report("Validando plano");
        await Task.Delay(stepDelay, cancellationToken);

        return new NexusReply($"Plano para guardar {quantity} {product.Unit} de {sku} de {from}.",
            DemoPlanService.ToDraft("putaway", "armazenagem", steps));
    }

    private async Task<NexusReply> PlanAdjustAsync(string request, string upper, string words, string? sku, List<string> locations,
        UserSession user, IProgress<string> status, CancellationToken cancellationToken)
    {
        if (sku is null || data.FindProduct(sku) is not { } product)
            return new NexusReply("Informe o SKU a ajustar (ex.: ALI-300).", IsError: true);
        if (locations.Count == 0)
            return new NexusReply("Informe o endereço do ajuste (ex.: C-01-01).", IsError: true);
        if (SignedQuantity(upper) is not { } signed || signed.Value == 0)
            return new NexusReply("Informe a quantidade do ajuste (ex.: -1).", IsError: true);

        // Without an explicit sign, losses ("avaria", "perda", "baixa") lower the balance.
        var quantity = !signed.ExplicitSign && ContainsAny(words, "avaria", "perda", "baixa") ? -signed.Value : signed.Value;

        var reasonIndex = request.ToLowerInvariant().IndexOf(" por ", StringComparison.Ordinal);
        var reason = reasonIndex >= 0 ? request[(reasonIndex + 5)..].Trim() : "";
        if (reason.Length == 0)
            return new NexusReply("Ajuste de estoque exige motivo (ex.: \"por avaria\").", IsError: true);

        status.Report("Planejando");
        await Task.Delay(stepDelay, cancellationToken);
        status.Report("Validando plano");
        await Task.Delay(stepDelay, cancellationToken);

        var step = new PlanStep(PlanOperation.AdjustStock, quantity, product.Unit, sku, locations[0], null, Capitalize(reason[..Math.Min(reason.Length, ApprovalPolicy.MaxReasonLength)]));
        return new NexusReply($"Plano de ajuste de {sku} em {locations[0]}. Ajuste é operação crítica e precisa de aprovação.",
            DemoPlanService.ToDraft("adjust", "inventario", [step]));
    }

    /// <summary>Fills zone A locations in order, skipping locations that hold another SKU.</summary>
    private List<PlanStep> Allocate(string sku, string unit, string from, int quantity)
    {
        var steps = new List<PlanStep>();
        foreach (var location in data.Locations.Where(l => l.ZoneCode == "A"))
        {
            if (quantity == 0)
                break;
            var holdsOther = data.Balances.Any(b => b.Location == location.Code && b.Sku != sku && b.Quantity > 0);
            var free = location.Capacity - data.OccupiedAt(location.Code);
            if (holdsOther || free <= 0)
                continue;
            var take = Math.Min(free, quantity);
            steps.Add(new PlanStep(PlanOperation.MoveStock, take, unit, sku, from, location.Code));
            quantity -= take;
        }
        return quantity == 0 ? steps : [];
    }

    private static int? FirstQuantity(string upper) =>
        QuantityPattern().Match(StripCodes(upper)) is { Success: true } m ? int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture) : null;

    private static (int Value, bool ExplicitSign)? SignedQuantity(string upper) =>
        SignedQuantityPattern().Match(StripCodes(upper)) is { Success: true } m
            ? (int.Parse(m.Value, CultureInfo.InvariantCulture), m.Value[0] is '+' or '-')
            : null;

    private static string StripCodes(string upper) => LocationPattern().Replace(SkuPattern().Replace(upper, " "), " ");

    private static bool ContainsAny(string text, params string[] keys) => keys.Any(text.Contains);

    private static string Capitalize(string text) => text.Length == 0 ? text : char.ToUpperInvariant(text[0]) + text[1..];

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

    [GeneratedRegex(@"\b(?:DOCA-\d{2}|[A-C]-\d{2}-\d{2})\b")]
    private static partial Regex LocationPattern();

    [GeneratedRegex(@"\b(\d{1,5})\b")]
    private static partial Regex QuantityPattern();

    [GeneratedRegex(@"[+-]?\d{1,5}\b")]
    private static partial Regex SignedQuantityPattern();
}
