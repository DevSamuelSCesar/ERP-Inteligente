using erp_inteligente.Models;
using Nexus.Tests.Fakes;

namespace Nexus.Tests;

public class DemoNexusServiceTests
{
    private static Task<erp_inteligente.Services.NexusReply> Ask(DemoWorld w, string request, UserSession? user = null) =>
        w.Nexus.SendAsync(request, user ?? DemoSessionService.Supervisor, DemoWorld.NoProgress);

    [Fact]
    public async Task Putaway_request_allocates_free_zone_A_locations()
    {
        var w = new DemoWorld();

        var reply = await Ask(w, "Guarde as 40 caixas do ABC-123 que estão na doca");

        Assert.NotNull(reply.Plan);
        Assert.Equal(PlanRisk.Reversible, reply.Plan.Risk);
        Assert.Collection(reply.Plan.Steps,
            s => Assert.Equal(("DOCA-01", "A-01-01", 20), (s.From, s.To, s.Quantity)),
            s => Assert.Equal(("DOCA-01", "A-02-01", 20), (s.From, s.To, s.Quantity)));
    }

    [Fact]
    public async Task Adjustment_request_builds_critical_plan_with_reason()
    {
        var w = new DemoWorld();

        var reply = await Ask(w, "ajuste -1 cx do ALI-300 em C-01-01 por avaria na embalagem");

        var step = Assert.Single(reply.Plan!.Steps);
        Assert.Equal(PlanRisk.Critical, reply.Plan.Risk);
        Assert.Equal((-1, "C-01-01", "Avaria na embalagem"), (step.Quantity, step.From, step.Note));
    }

    [Fact]
    public async Task Loss_without_sign_lowers_the_balance()
    {
        var w = new DemoWorld();

        var reply = await Ask(w, "avaria de 2 cx do ALI-300 em C-01-01 por caixa amassada");

        Assert.Equal(-2, Assert.Single(reply.Plan!.Steps).Quantity);
    }

    [Fact]
    public async Task Adjustment_without_reason_is_refused()
    {
        var w = new DemoWorld();

        var reply = await Ask(w, "ajuste -1 do ALI-300 em C-01-01");

        Assert.True(reply.IsError);
        Assert.Null(reply.Plan);
    }

    [Fact]
    public async Task Stock_question_is_answered_without_plan()
    {
        var w = new DemoWorld();

        var reply = await Ask(w, "qual o saldo do ABC-123?");

        Assert.Null(reply.Plan);
        Assert.Contains("40 cx em DOCA-01", reply.Text);
    }

    [Fact]
    public async Task User_without_plan_submit_is_refused()
    {
        var w = new DemoWorld();
        var auditor = DemoSessionService.Operator with { Username = "aud.lucia", Permissions = new HashSet<string> { "stock.read", "audit.read" } };

        var reply = await Ask(w, "saldo do ABC-123", auditor);

        Assert.True(reply.IsError);
    }

    [Fact]
    public async Task Unknown_request_gets_plain_guidance()
    {
        var w = new DemoWorld();

        var reply = await Ask(w, "bom dia");

        Assert.Null(reply.Plan);
        Assert.False(reply.IsError);
        Assert.StartsWith("Não entendi", reply.Text);
    }

    [Fact]
    public async Task Too_long_request_is_refused()
    {
        var w = new DemoWorld();

        var reply = await Ask(w, new string('a', 2001));

        Assert.True(reply.IsError);
    }
}
