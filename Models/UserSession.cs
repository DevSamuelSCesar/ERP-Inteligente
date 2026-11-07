using System.Collections.Generic;

namespace erp_inteligente.Models;

/// <summary>
/// Authenticated user with effective permissions already resolved (deny-overrides). Every application
/// query is scoped to <see cref="WarehouseId"/> (references/ARQUITETURA.md, section 12.5).
/// </summary>
public sealed record UserSession(
    long UserId,
    string Username,
    string DisplayName,
    long RoleId,
    string RoleName,
    long WarehouseId,
    string WarehouseCode,
    IReadOnlySet<string> Permissions)
{
    public bool Has(string permission) => Permissions.Contains(permission);
}
