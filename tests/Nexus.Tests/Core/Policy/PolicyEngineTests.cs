using erp_inteligente.Core.Audit;
using erp_inteligente.Core.Policy;
using erp_inteligente.Models;

namespace Nexus.Tests.Core.Policy;

public class PolicyEngineTests : IDisposable
{
    private readonly PolicyTestFixture _f = new();

    public void Dispose() => _f.Dispose();

    [Fact]
    public void Supervisor_matches_seed_matrix()
    {
        var p = _f.Policy.PermissionsForRole(2);
        Assert.Contains("plan.approve", p);
        Assert.Contains("stock.adjust", p);
        Assert.Contains("audit.read", p);
        Assert.DoesNotContain("user.manage", p);
        Assert.DoesNotContain("role.manage", p);
    }

    [Fact]
    public void Operator_matches_seed_matrix()
    {
        var p = _f.Policy.PermissionsForRole(1);
        Assert.Contains("stock.read", p);
        Assert.Contains("plan.submit", p);
        Assert.DoesNotContain("plan.approve", p);
        Assert.DoesNotContain("stock.adjust", p);
        Assert.DoesNotContain("audit.read", p);
    }

    [Fact]
    public void Deny_inserted_takes_effect_immediately_and_require_audits()
    {
        var sup = _f.SessionFor("sup.maria");
        Assert.True(_f.Policy.IsAllowed(sup, "stock.read"));
        _f.Exec("INSERT INTO role_permissions (role_id, permission, effect) VALUES (2, 'stock.read', 'DENY')");

        Assert.False(_f.Policy.IsAllowed(sup, "stock.read")); // stale cached set still says yes
        Assert.True(sup.Has("stock.read"));
        var ex = Assert.Throws<PolicyDeniedException>(() => _f.Policy.Require(sup, "stock.read"));
        Assert.Equal("Permissão negada: stock.read", ex.Message);

        Assert.Equal(1, _f.Scalar(
            "SELECT count(*) FROM audit_log WHERE action = 'policy.denied' AND target = 'permission:stock.read' " +
            "AND outcome = 'denied' AND actor_type = 'user' AND actor_id = $id", ("$id", sup.UserId)));
        Assert.True(AuditVerifier.Verify(_f.Open()).IsValid);
    }

    [Fact]
    public void Require_throws_for_missing_permission()
    {
        var op = _f.SessionFor("op.joao");
        Assert.Throws<PolicyDeniedException>(() => _f.Policy.Require(op, Permissions.AuditRead));
        _f.Policy.Require(op, Permissions.StockRead); // allowed: no throw
    }

    [Fact]
    public void EffectiveForAi_is_intersection()
    {
        var op = _f.SessionFor("op.joao");
        var sup = _f.SessionFor("sup.maria");
        var ai = _f.Policy.PermissionsForRole(5);

        var effective = _f.Policy.EffectiveForAi(op);
        Assert.DoesNotContain("plan.approve", effective);
        Assert.DoesNotContain("stock.adjust", effective);
        Assert.DoesNotContain("audit.read", effective);
        Assert.Contains("stock.move", effective);
        Assert.True(effective.IsSubsetOf(ai));
        Assert.True(effective.IsSubsetOf(_f.Policy.PermissionsForRole(1)));

        var forSup = _f.Policy.EffectiveForAi(sup);
        Assert.DoesNotContain("plan.approve", forSup);
        Assert.True(forSup.IsSubsetOf(ai));
        Assert.True(forSup.IsSubsetOf(_f.Policy.PermissionsForRole(2)));
    }
}
