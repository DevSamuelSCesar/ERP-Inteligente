using erp_inteligente.Core.Audit;
using erp_inteligente.Models;
using erp_inteligente.Services;
using erp_inteligente.Services.Sqlite;
using Nexus.Tests.Core.Policy;

namespace Nexus.Tests.Services;

/// <summary>The Nexus before the planner exists: stock questions only, through the AI connection, audited.</summary>
public class ReadOnlyNexusServiceTests : IDisposable
{
    private const string AbcStockReply = "ABC-123 (Caixa de parafusos 4mm): 40 cx em A-01-01; 40 cx em DOCA-01.";
    private const string ChangesReply =
        "Alterar dados chega com o motor de planos (feature 2 do roadmap). Por enquanto o Nexus só consulta saldos.";
    private const string RefusedReply = "Seu papel não permite consultas pelo Nexus.";

    private readonly PolicyTestFixture _f = new();
    private readonly ReadOnlyNexusService _nexus;

    public ReadOnlyNexusServiceTests() =>
        _nexus = new ReadOnlyNexusService(_f.Db.Database, _f.Policy, _f.Audit);

    public void Dispose() => _f.Dispose();

    private Task<NexusReply> Ask(string request, string username, StatusLog? status = null) =>
        _nexus.SendAsync(request, _f.SessionFor(username), status ?? new StatusLog(), TestContext.Current.CancellationToken);

    private long AiQueryAudit(string target, string outcome, long onBehalfOf) => _f.Scalar(
        "SELECT count(*) FROM audit_log WHERE actor_type = 'ai' AND actor_id IS NULL AND action = 'ai.query' " +
        "AND target = $target AND outcome = $outcome AND on_behalf_of = $user",
        ("$target", target), ("$outcome", outcome), ("$user", onBehalfOf));

    [Fact]
    public async Task Stock_question_lists_both_locations_of_the_sku_and_audits_the_read_on_behalf_of_the_user()
    {
        var status = new StatusLog();
        var session = _f.SessionFor("sup.maria");

        var reply = await _nexus.SendAsync("Qual o saldo do ABC-123?", session, status, TestContext.Current.CancellationToken);

        Assert.False(reply.IsError);
        Assert.Null(reply.Plan);
        Assert.Equal(AbcStockReply, reply.Text);
        Assert.Equal("Consultando saldo do ABC-123", Assert.Single(status.Lines));
        Assert.Equal(1, AiQueryAudit("sku:ABC-123", "allowed", session.UserId));
        using var connection = _f.Open();
        Assert.True(AuditVerifier.Verify(connection).IsValid);
    }

    [Fact]
    public async Task Operator_can_ask_for_stock_because_the_operator_role_has_stock_read()
    {
        var reply = await Ask("saldo do ABC-124", "op.joao");

        Assert.False(reply.IsError);
        Assert.Equal("ABC-124 (Caixa de porcas 4mm): 35 cx em A-01-02.", reply.Text);
    }

    [Fact]
    public async Task Auditor_can_ask_for_stock_because_both_the_auditor_role_and_the_ai_role_allow_stock_read()
    {
        var reply = await Ask("saldo do ABC-123", "aud.lucia");

        Assert.False(reply.IsError);
        Assert.Equal(AbcStockReply, reply.Text);
    }

    [Fact]
    public async Task Change_request_gets_a_plain_answer_without_a_plan_and_changes_nothing()
    {
        var stockBefore = _f.Scalar("SELECT SUM(quantity) FROM stock_balances");
        var movementsBefore = _f.Scalar("SELECT count(*) FROM stock_movements");

        var reply = await Ask("Guarde as 40 caixas do ABC-123 que estão na doca", "sup.maria");

        Assert.False(reply.IsError);
        Assert.Null(reply.Plan);
        Assert.Equal(ChangesReply, reply.Text);
        Assert.Equal(stockBefore, _f.Scalar("SELECT SUM(quantity) FROM stock_balances"));
        Assert.Equal(movementsBefore, _f.Scalar("SELECT count(*) FROM stock_movements"));
    }

    [Fact]
    public async Task Unknown_request_gets_guidance_about_stock_queries_only()
    {
        var reply = await Ask("bom dia", "sup.maria");

        Assert.False(reply.IsError);
        Assert.Null(reply.Plan);
        Assert.StartsWith("Não entendi", reply.Text);
        Assert.Contains("saldo", reply.Text);
    }

    [Fact]
    public async Task Empty_request_is_refused()
    {
        var reply = await Ask("   ", "sup.maria");

        Assert.True(reply.IsError);
        Assert.Equal("Escreva um pedido.", reply.Text);
    }

    [Fact]
    public async Task Too_long_request_is_refused()
    {
        var reply = await Ask(new string('a', 2001), "sup.maria");

        Assert.True(reply.IsError);
    }

    [Fact]
    public async Task Unknown_sku_is_refused_and_the_read_is_still_audited()
    {
        var reply = await Ask("saldo do ZZZ-999", "sup.maria");

        Assert.True(reply.IsError);
        Assert.Equal("SKU ZZZ-999 não existe.", reply.Text);
        Assert.Equal(1, AiQueryAudit("sku:ZZZ-999", "allowed", _f.SessionFor("sup.maria").UserId));
    }

    [Fact]
    public async Task Sku_without_balance_is_reported_as_out_of_stock()
    {
        _f.Exec("INSERT INTO products (sku, name, unit) VALUES ('MAR-001', 'Martelo de unha', 'un')");

        var reply = await Ask("saldo do MAR-001", "sup.maria");

        Assert.False(reply.IsError);
        Assert.Equal("MAR-001 (Martelo de unha) está sem saldo.", reply.Text);
    }

    [Fact]
    public async Task Stock_of_another_warehouse_is_not_described()
    {
        SqliteTestData.AddSecondWarehouse(_f);

        var reply = await Ask("saldo do ABC-123", "sup.maria");

        Assert.Equal(AbcStockReply, reply.Text);
        Assert.DoesNotContain("DOCA-99", reply.Text);
    }

    [Fact]
    public async Task Role_without_stock_read_is_refused_and_the_denial_is_audited()
    {
        _f.Exec("INSERT INTO role_permissions (role_id, permission, effect) VALUES (1, 'stock.read', 'DENY')");
        var session = _f.SessionFor("op.joao");

        var reply = await _nexus.SendAsync("saldo do ABC-123", session, new StatusLog(), TestContext.Current.CancellationToken);

        Assert.True(reply.IsError);
        Assert.Equal(RefusedReply, reply.Text);
        Assert.Equal(1, _f.Scalar(
            "SELECT count(*) FROM audit_log WHERE actor_type = 'ai' AND action = 'ai.query' AND outcome = 'denied' " +
            "AND target = 'permission:stock.read' AND on_behalf_of = $user", ("$user", session.UserId)));
        Assert.Equal(0, AiQueryAudit("sku:ABC-123", "allowed", session.UserId));
    }

    [Fact]
    public async Task Ai_role_without_stock_read_refuses_even_when_the_user_has_it()
    {
        // The AI never has more than the user: removing stock.read from the AI role blocks the operator, who has it.
        _f.Exec("DELETE FROM role_permissions WHERE role_id = 5 AND permission = 'stock.read' AND effect = 'ALLOW'");

        var reply = await Ask("saldo do ABC-123", "op.joao");

        Assert.True(reply.IsError);
        Assert.Equal(RefusedReply, reply.Text);
    }
}
