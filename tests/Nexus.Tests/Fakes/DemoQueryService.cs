using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using erp_inteligente.Models;
using erp_inteligente.Services;

namespace Nexus.Tests.Fakes;

public sealed class DemoQueryService(DemoData data) : IQueryService
{
    public Task<IReadOnlyList<StockRow>> GetStockAsync()
    {
        IReadOnlyList<StockRow> rows = data.Balances
            .Where(b => b.Quantity > 0)
            .OrderBy(b => b.Sku).ThenBy(b => b.Location)
            .Select(b =>
            {
                var product = data.FindProduct(b.Sku);
                return new StockRow(b.Sku, product?.Name ?? "", b.Location, b.Lot, b.ExpiresOn, b.Quantity, b.Reserved, product?.Unit ?? "");
            })
            .ToList();
        return Task.FromResult(rows);
    }

    public Task<IReadOnlyList<ProductRow>> GetProductsAsync()
    {
        IReadOnlyList<ProductRow> rows = data.Products
            .OrderBy(p => p.Sku)
            .Select(p => new ProductRow(p.Sku, p.Name, p.Unit, null, false, "",
                data.Balances.Where(b => b.Sku == p.Sku).Sum(b => b.Quantity)))
            .ToList();
        return Task.FromResult(rows);
    }

    public Task<IReadOnlyList<LocationRow>> GetLocationsAsync()
    {
        IReadOnlyList<LocationRow> rows = data.Locations
            .Select(l => new LocationRow(l.Code, data.Zones[l.ZoneCode], l.Capacity, data.OccupiedAt(l.Code)))
            .ToList();
        return Task.FromResult(rows);
    }

    public Task<IReadOnlyList<OrderRow>> GetOrdersAsync() => Task.FromResult<IReadOnlyList<OrderRow>>(data.Orders.ToList());

    public Task<IReadOnlyList<RecipeRow>> GetRecipesAsync() => Task.FromResult<IReadOnlyList<RecipeRow>>(data.Recipes.ToList());

    public Task<IReadOnlyList<AuditRow>> GetAuditAsync() =>
        Task.FromResult<IReadOnlyList<AuditRow>>(data.Audit.OrderByDescending(a => a.At).ToList());
}
