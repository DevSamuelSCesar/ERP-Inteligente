using System.Collections.Generic;

namespace erp_inteligente.Models;

/// <summary>Authenticated user with effective permissions already resolved (deny-overrides).</summary>
public sealed record UserSession(
    string Username,
    string DisplayName,
    string RoleName,
    string WarehouseCode,
    IReadOnlySet<string> Permissions)
{
    public bool Has(string permission) => Permissions.Contains(permission);
}
