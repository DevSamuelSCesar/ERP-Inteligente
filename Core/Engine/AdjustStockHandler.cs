using System;
using System.Collections.Generic;
using System.Text.Json.Nodes;
using static erp_inteligente.Core.Engine.StockStore;

namespace erp_inteligente.Core.Engine;

/// <summary>adjust_stock: signed correction of a balance (inventory count, damage). Never below the reserved quantity.</summary>
internal sealed class AdjustStockHandler : IOperationHandler
{
    private sealed record Plan(ProductRow Product, LocationRow Location, int Qty, string Reason, long? LotId, string? LotCode);

    public string Op => "adjust_stock";

    public IReadOnlyList<PlanError> Validate(EngineContext ctx, PlanStepDocument step, JsonObject args)
    {
        var errors = new List<PlanError>();
        Resolve(ctx, step, args, errors);
        return errors;
    }

    public string Apply(EngineContext ctx, PlanStepDocument step, JsonObject args)
    {
        var errors = new List<PlanError>();
        var p = Resolve(ctx, step, args, errors) ?? throw new InvalidOperationException("Step applied without validation.");
        var (before, after) = AddToBalance(ctx, p.Product.Id, p.Location.Id, p.LotId, p.Qty);
        InsertMovement(ctx, p.Product.Id, p.LotId,
            p.Qty < 0 ? p.Location.Id : null, p.Qty > 0 ? p.Location.Id : null, "adjust", p.Qty, p.Reason);
        ctx.Diff.AddDirectBalance(p.Product.Sku, p.Location.Code, p.LotCode, before, after, p.Product.Unit);
        ctx.Diff.AddAdjustment(p.Product.Sku, p.Qty, p.Product.Unit);
        return p.Location.Code;
    }

    private static Plan? Resolve(EngineContext ctx, PlanStepDocument step, JsonObject args, List<PlanError> errors)
    {
        var sku = Str(args, "sku");
        var product = FindProduct(ctx, sku);
        if (product is null)
        {
            errors.Add(Error(step, "sku", "business.unknown_sku", $"o SKU {sku} não existe no cadastro."));
            return null;
        }
        var locCode = Str(args, "location");
        var location = FindLocation(ctx, locCode);
        if (location is null)
        {
            errors.Add(Error(step, "location", "business.unknown_location", UnknownLocation(ctx, locCode)));
            return null;
        }
        var qty = Int(args, "qty") ?? 0;
        if (qty == 0)
        {
            errors.Add(Error(step, "qty", "business.invalid_qty", "o ajuste não pode ser zero."));
            return null;
        }
        var reason = Str(args, "reason");
        if (string.IsNullOrWhiteSpace(reason))
        {
            errors.Add(Error(step, "reason", "business.reason_required", "o ajuste exige um motivo."));
            return null;
        }

        var lotCode = Str(args, "lot");
        var rows = Balances(ctx, product.Id, location.Id);
        var pick = PickBalance(rows, lotCode, allowEmpty: qty > 0, out var row);
        if (pick == Pick.NeedLot)
        {
            errors.Add(Error(step, "lot", "business.lot_required",
                $"{product.Sku} tem mais de um lote em {location.Code}; informe o lote a ajustar."));
            return null;
        }

        long? lotId = row?.LotId;
        if (row is null)
        {
            if (qty < 0)
            {
                errors.Add(Error(step, "location", "business.no_stock", lotCode is null
                    ? $"não há saldo de {product.Sku} em {location.Code} para baixar."
                    : $"não há saldo do lote {lotCode} de {product.Sku} em {location.Code} para baixar."));
                return null;
            }
            if (lotCode is not null)
            {
                var lot = FindLot(ctx, product.Id, lotCode);
                if (lot is null)
                {
                    errors.Add(Error(step, "lot", "business.unknown_lot", $"o lote {lotCode} não existe para o SKU {product.Sku}."));
                    return null;
                }
                lotId = lot.Id;
            }
        }

        var before = errors.Count;
        if (qty < 0)
        {
            if (row!.Quantity + qty < row.Reserved)
                errors.Add(Error(step, "qty", "business.below_reserved", row.Quantity + qty < 0
                    ? $"o ajuste deixaria o saldo negativo (saldo {row.Quantity})."
                    : $"o ajuste deixaria o saldo abaixo do reservado (saldo {row.Quantity}, reservado {row.Reserved})."));
        }
        else
        {
            var occupied = Occupied(ctx, location.Id);
            if (occupied + qty > location.Capacity)
                errors.Add(Error(step, "qty", "business.capacity", OutOfCapacity(location, occupied, qty)));
        }
        return errors.Count > before ? null : new Plan(product, location, qty, reason, lotId, row?.LotCode ?? lotCode);
    }
}
