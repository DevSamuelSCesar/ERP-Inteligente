using System;
using System.Collections.Generic;
using System.Text.Json.Nodes;
using static erp_inteligente.Core.Engine.StockStore;

namespace erp_inteligente.Core.Engine;

/// <summary>move_stock: transfers available balance between two locations of the warehouse.</summary>
internal sealed class MoveStockHandler : IOperationHandler
{
    private sealed record Plan(ProductRow Product, LocationRow From, LocationRow To, int Qty, BalanceRow Source);

    public string Op => "move_stock";

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
        var sku = p.Product.Sku;
        var unit = p.Product.Unit;

        var (srcBefore, srcAfter) = AddToBalance(ctx, p.Product.Id, p.From.Id, p.Source.LotId, -p.Qty);
        var (dstBefore, dstAfter) = AddToBalance(ctx, p.Product.Id, p.To.Id, p.Source.LotId, p.Qty);
        InsertMovement(ctx, p.Product.Id, p.Source.LotId, p.From.Id, p.To.Id, "move", p.Qty, null);
        ctx.Diff.AddMoveBalance(sku, p.From.Code, p.Source.LotCode, srcBefore, srcAfter, unit);
        ctx.Diff.AddMoveBalance(sku, p.To.Code, p.Source.LotCode, dstBefore, dstAfter, unit);
        ctx.Diff.AddMove(sku, p.Source.LotCode, p.From.Code, p.To.Code, p.Qty, unit);
        return p.To.Code;
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
        var fromCode = Str(args, "from");
        var toCode = Str(args, "to");
        var from = FindLocation(ctx, fromCode);
        var to = FindLocation(ctx, toCode);
        if (from is null)
            errors.Add(Error(step, "from", "business.unknown_location", UnknownLocation(ctx, fromCode)));
        if (to is null)
            errors.Add(Error(step, "to", "business.unknown_location", UnknownLocation(ctx, toCode)));
        if (from is null || to is null) return null;

        var qty = Int(args, "qty") ?? 0;
        if (qty < 1)
        {
            errors.Add(Error(step, "qty", "business.invalid_qty", "a quantidade a mover deve ser maior que zero."));
            return null;
        }
        if (from.Id == to.Id)
        {
            errors.Add(Error(step, "to", "business.same_location", "a origem e o destino são o mesmo endereço."));
            return null;
        }

        var lotCode = Str(args, "lot");
        var rows = Balances(ctx, product.Id, from.Id);
        switch (PickBalance(rows, lotCode, allowEmpty: false, out var source))
        {
            case Pick.NeedLot:
                errors.Add(Error(step, "lot", "business.lot_required",
                    $"{product.Sku} tem mais de um lote em {from.Code}; informe o lote a mover."));
                return null;
            case Pick.NoStock:
                errors.Add(Error(step, "from", "business.no_stock", lotCode is null
                    ? $"não há saldo de {product.Sku} em {from.Code}."
                    : $"não há saldo do lote {lotCode} de {product.Sku} em {from.Code}."));
                return null;
        }

        var before = errors.Count;
        if (source!.Available < qty)
            errors.Add(Error(step, "qty", "business.insufficient_stock",
                $"saldo disponível insuficiente de {product.Sku} em {from.Code} (disponível {source.Available}, pedido {qty})."));
        var occupied = Occupied(ctx, to.Id);
        if (occupied + qty > to.Capacity)
            errors.Add(Error(step, "qty", "business.capacity", OutOfCapacity(to, occupied, qty)));
        return errors.Count > before ? null : new Plan(product, from, to, qty, source);
    }
}
