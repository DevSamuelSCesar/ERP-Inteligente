using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using erp_inteligente.Models;
using erp_inteligente.Services;

namespace erp_inteligente.ViewModels;

public sealed class StockViewModel(IQueryService queries, TimeProvider time) : QueryPageViewModel<StockRow>
{
    /// <summary>Lots expiring within this many days are counted in the totals strip.</summary>
    public const int ExpiryWindowDays = 30;

    public override string Title => "Estoque";
    public override string FilterPlaceholder => "Filtrar por SKU, produto, endereço ou lote";

    protected override Task<IReadOnlyList<StockRow>> LoadAsync() => queries.GetStockAsync();

    protected override IEnumerable<string> SearchableText(StockRow row) => [row.Sku, row.Product, row.Location, row.Lot];

    protected override IEnumerable<SummaryTotal> BuildTotals(IReadOnlyList<StockRow> rows)
    {
        var today = DateOnly.FromDateTime(time.GetLocalNow().Date);
        yield return new("Saldo total", rows.Sum(r => r.Quantity).ToString("N0"));
        yield return new("Reservado", rows.Sum(r => r.Reserved).ToString("N0"));
        yield return new("SKUs", rows.Select(r => r.Sku).Distinct().Count().ToString());
        yield return new("Endereços ocupados", rows.Select(r => r.Location).Distinct().Count().ToString());
        yield return new($"Lotes a vencer em {ExpiryWindowDays} dias",
            rows.Count(r => r.ExpiresOn is { } d && d.DayNumber - today.DayNumber <= ExpiryWindowDays).ToString());
    }
}

public sealed class ProductsViewModel(IQueryService queries) : QueryPageViewModel<ProductRow>
{
    public override string Title => "Produtos";
    public override string FilterPlaceholder => "Filtrar por SKU, nome ou descrição";

    protected override Task<IReadOnlyList<ProductRow>> LoadAsync() => queries.GetProductsAsync();

    protected override IEnumerable<string> SearchableText(ProductRow row) => [row.Sku, row.Name, row.Description];

    protected override IEnumerable<SummaryTotal> BuildTotals(IReadOnlyList<ProductRow> rows)
    {
        yield return new("Produtos", rows.Count.ToString());
        yield return new("Com saldo", rows.Count(r => r.Balance > 0).ToString());
        yield return new("Sem saldo", rows.Count(r => r.Balance == 0).ToString());
        yield return new("Controlam validade", rows.Count(r => r.TracksExpiry).ToString());
    }
}

public sealed class LocationsViewModel(IQueryService queries) : QueryPageViewModel<LocationRow>
{
    public override string Title => "Endereços";
    public override string FilterPlaceholder => "Filtrar por endereço ou zona";

    protected override Task<IReadOnlyList<LocationRow>> LoadAsync() => queries.GetLocationsAsync();

    protected override IEnumerable<string> SearchableText(LocationRow row) => [row.Code, row.Zone];

    protected override IEnumerable<SummaryTotal> BuildTotals(IReadOnlyList<LocationRow> rows)
    {
        yield return new("Endereços", rows.Count.ToString());
        yield return new("Capacidade", rows.Sum(r => r.Capacity).ToString("N0"));
        yield return new("Ocupado", rows.Sum(r => r.Occupied).ToString("N0"));
        yield return new("Vazios", rows.Count(r => r.Occupied == 0).ToString());
    }
}

public sealed class ZonesViewModel(IQueryService queries) : QueryPageViewModel<ZoneRow>
{
    public override string Title => "Zonas";
    public override string FilterPlaceholder => "Filtrar por código ou nome";

    protected override Task<IReadOnlyList<ZoneRow>> LoadAsync() => queries.GetZonesAsync();

    protected override IEnumerable<string> SearchableText(ZoneRow row) => [row.Code, row.Name, row.StatusText];

    protected override IEnumerable<SummaryTotal> BuildTotals(IReadOnlyList<ZoneRow> rows)
    {
        yield return new("Zonas", rows.Count.ToString());
        yield return new("Ativas", rows.Count(r => r.IsActive).ToString());
        yield return new("Endereços", rows.Sum(r => r.Locations).ToString("N0"));
        yield return new("Espaço livre", rows.Sum(r => r.Free).ToString("N0"));
    }
}

public sealed class PartnersViewModel(IQueryService queries) : QueryPageViewModel<PartnerRow>
{
    public override string Title => "Parceiros";
    public override string FilterPlaceholder => "Filtrar por nome, tipo ou documento";

    protected override Task<IReadOnlyList<PartnerRow>> LoadAsync() => queries.GetPartnersAsync();

    protected override IEnumerable<string> SearchableText(PartnerRow row) => [row.Name, row.Kind, row.Document];

    protected override IEnumerable<SummaryTotal> BuildTotals(IReadOnlyList<PartnerRow> rows)
    {
        yield return new("Fornecedores", rows.Count(r => r.Kind == "Fornecedor").ToString());
        yield return new("Clientes", rows.Count(r => r.Kind == "Cliente").ToString());
        yield return new("Desativados", rows.Count(r => !r.IsActive).ToString());
    }
}

public sealed class OrdersViewModel(IQueryService queries) : QueryPageViewModel<OrderRow>
{
    public override string Title => "Pedidos";
    public override string FilterPlaceholder => "Filtrar por número, tipo, fornecedor ou cliente";

    protected override Task<IReadOnlyList<OrderRow>> LoadAsync() => queries.GetOrdersAsync();

    protected override IEnumerable<string> SearchableText(OrderRow row) => [row.Number, row.Kind, row.Party, row.Status];

    protected override IEnumerable<SummaryTotal> BuildTotals(IReadOnlyList<OrderRow> rows)
    {
        yield return new("Pedidos", rows.Count.ToString());
        yield return new("Entrada", rows.Count(r => r.Kind == "Entrada").ToString());
        yield return new("Saída", rows.Count(r => r.Kind == "Saída").ToString());
        yield return new("Unidades", rows.Sum(r => r.Units).ToString("N0"));
    }
}

