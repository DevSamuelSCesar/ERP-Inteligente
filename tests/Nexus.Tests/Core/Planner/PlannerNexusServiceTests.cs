using erp_inteligente.Core.Engine;
using erp_inteligente.Core.Llm;
using erp_inteligente.Core.Planner;
using erp_inteligente.Models;
using erp_inteligente.Services.Sqlite;
using Nexus.Tests.Core.Engine;
using Nexus.Tests.Core.Policy;
using PlannerClass = erp_inteligente.Core.Planner.Planner;

namespace Nexus.Tests.Core.Planner;

/// <summary>The mapping from the Planner results to what the UI consumes (<see cref="erp_inteligente.Services.NexusReply"/>).</summary>
public sealed class PlannerNexusServiceTests : IDisposable
{
    private readonly PolicyTestFixture _f = new();
    private readonly ScriptedLlm _llm = new();

    public void Dispose() => _f.Dispose();

    private PlannerNexusService NewService() =>
        new(new PlannerClass(_llm, new LlmSettings(_f.Db.Store, "nexus-service-tests"), _f.Db.Database, _f.Policy, _f.Audit,
            new PlanEngine(_f.Db.Database, _f.Policy, _f.Audit, _f.Time)));

    private static LlmResponse Text(string text) => new(text, [], null, LlmFinishReason.Stop, new LlmUsage(10, 10));

    private static LlmResponse Calls(params LlmToolCall[] calls) => new(null, calls, null, LlmFinishReason.ToolCalls, new LlmUsage(10, 10));

    private static string Plan(string title, string to) =>
        System.Text.Json.JsonSerializer.Serialize(new
        {
            title,
            rationale = "Justificativa " + title,
            assumptions = new[] { "suposição " + title },
            plan = "{\"dsl_version\":\"1.0\",\"intent\":\"putaway\",\"steps\":[" + EngineHarness.Move("ABC-123", 10, "DOCA-01", to) + "]}",
        });

    [Fact]
    public async Task Proposals_and_options_are_mapped_for_the_ui()
    {
        _llm.Enqueue(Calls(
                new LlmToolCall("1", "propose_plan", Plan("Plano A", "A-02-01")),
                new LlmToolCall("2", "propose_plan", Plan("Plano B", "A-02-02")),
                new LlmToolCall("3", "offer_options", """{"options":[{"label":"Outro","request":"faça outro"}]}""")))
            .Enqueue(Text("Duas opções."));
        var reply = await NewService().SendAsync("guarde", _f.SessionFor("op.joao"), new Progress<string>(), TestContext.Current.CancellationToken);

        Assert.Equal("Duas opções.", reply.Text);
        Assert.False(reply.IsError || reply.IsEscalated);
        Assert.Equal(["Plano A", "Plano B"], reply.Proposals!.Select(p => p.Title));
        var first = reply.Proposals![0];
        Assert.Equal("Justificativa Plano A", first.Rationale);
        Assert.Equal(["suposição Plano A"], first.Assumptions);
        Assert.Equal(PlanOrigin.Ai, first.Draft.Origin);
        Assert.Contains("A-02-01", first.Draft.Json);
        Assert.Equal([new erp_inteligente.Services.NexusOption("Outro", "faça outro")], reply.Options);
    }

    [Fact]
    public async Task Answer_with_options_is_mapped()
    {
        _llm.Enqueue(Calls(new LlmToolCall("1", "offer_options", """{"options":[{"label":"A","request":"a"},{"label":"B","request":"b"}]}""")))
            .Enqueue(Text("Qual?"));
        var reply = await NewService().SendAsync("oi", _f.SessionFor("op.joao"), new Progress<string>(), TestContext.Current.CancellationToken);

        Assert.Equal("Qual?", reply.Text);
        Assert.Null(reply.Proposals);
        Assert.Equal(["A", "B"], reply.Options!.Select(o => o.Label));
    }

    [Fact]
    public async Task Plain_answer_has_no_options_and_failures_keep_their_flags()
    {
        _llm.Enqueue(Text("oi")).Throw(new LlmException(LlmErrorKind.Unavailable, "x"));
        var service = NewService();
        var user = _f.SessionFor("op.joao");

        var answer = await service.SendAsync("oi", user, new Progress<string>(), TestContext.Current.CancellationToken);
        Assert.Empty(answer.Options ?? []);
        Assert.Null(answer.Proposals);

        var failed = await service.SendAsync("de novo", user, new Progress<string>(), TestContext.Current.CancellationToken);
        Assert.True(failed.IsError);
    }
}
