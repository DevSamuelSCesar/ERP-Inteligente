using System.Collections.Generic;
using System.Threading.Tasks;
using erp_inteligente.Models;

namespace erp_inteligente.Services;

/// <summary>Read-only queries scoped to the warehouse of the current session.</summary>
public interface IQueryService
{
    Task<IReadOnlyList<StockRow>> GetStockAsync();
    Task<IReadOnlyList<LocationRow>> GetLocationsAsync();
    Task<IReadOnlyList<OrderRow>> GetOrdersAsync();
    Task<IReadOnlyList<RecipeRow>> GetRecipesAsync();
    Task<IReadOnlyList<AuditRow>> GetAuditAsync();
}
