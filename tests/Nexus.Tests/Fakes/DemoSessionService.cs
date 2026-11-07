using System.Collections.Generic;
using erp_inteligente.Models;
using erp_inteligente.Services;

namespace Nexus.Tests.Fakes;

/// <summary>Fixed session for the UI-only build; login arrives with ROADMAP.md feature 1.</summary>
public sealed class DemoSessionService(UserSession current) : ISessionService
{
    public UserSession Current { get; } = current;

    /// <summary>Maria, supervisor of CD-01, with the effective rights of the supervisor role in db/seed.sql.</summary>
    public static UserSession Supervisor { get; } = new(2, "sup.maria", "Maria", 2, "Supervisora", 1, "CD-01",
        new HashSet<string>
        {
            "stock.read", "stock.receive", "stock.move", "stock.reserve", "stock.pick", "stock.ship",
            "stock.adjust", "plan.submit", "plan.approve", "catalog.manage", "location.manage",
            "sensitive.read", "memory.write", "recipe.manage", "audit.read",
        });

    /// <summary>João, operator of CD-01 (no approval, no audit).</summary>
    public static UserSession Operator { get; } = new(1, "op.joao", "João", 1, "Operador", 1, "CD-01",
        new HashSet<string>
        {
            "stock.read", "stock.receive", "stock.move", "stock.reserve", "stock.pick", "plan.submit", "memory.write",
        });
}
