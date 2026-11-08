using System.Text.Json;
using erp_inteligente.Core.Engine;
using erp_inteligente.Core.Llm;
using erp_inteligente.Core.Planner;
using erp_inteligente.Core.Skills;
using erp_inteligente.Models;
using erp_inteligente.Services.Sqlite;
using Nexus.Tests.Core.Engine;
using Nexus.Tests.Core.Policy;
using PlannerClass = erp_inteligente.Core.Planner.Planner;

namespace Nexus.Tests.Core.Planner;

/// <summary>The skills in the Planner: index in the prompt, load_skill, the plan skill field and propose_skill.</summary>
public sealed class SkillToolsTests : IDisposable
{
    private const string BuiltInSentence = "Escolha a zona pelo tipo do produto";

    private readonly PolicyTestFixture _f = new();
    private readonly ScriptedLlm _llm = new();
    private readonly UserSession _joao;
    private readonly UserSession _maria;
    private readonly SkillStore _store;

    public SkillToolsTests()
    {
        _joao = _f.SessionFor("op.joao");
        _maria = _f.SessionFor("sup.maria");
        _store = new SkillStore(_f.Db.Database, _f.Policy, _f.Audit, _f.Time);
    }

    public void Dispose() => _f.Dispose();

    private PlannerClass NewPlanner(bool withSkills = true) =>
        new(_llm, new LlmSettings(_f.Db.Store, "skill-tests"), _f.Db.Database, _f.Policy, _f.Audit,
            new PlanEngine(_f.Db.Database, _f.Policy, _f.Audit, _f.Time), skills: withSkills ? new SkillCatalog(_f.Db.Database) : null);

    private sealed class Status : IProgress<string>
    {
        public List<string> Lines { get; } = [];
        public void Report(string value) => Lines.Add(value);
    }

    private Task<(PlannerResult Result, PlannerTurn? Turn)> Run(PlannerClass planner, UserSession user, string request = "faça", Status? status = null) =>
        planner.RunAsync(user, [], request, status ?? new Status(), TestContext.Current.CancellationToken);

    private static LlmResponse Text(string text) => new(text, [], null, LlmFinishReason.Stop, new LlmUsage(100, 10));

    private static LlmResponse Calls(params LlmToolCall[] calls) => new(null, calls, null, LlmFinishReason.ToolCalls, new LlmUsage(100, 10));

    private static LlmToolCall Call(string name, string args, string id = "c1") => new(id, name, args);

    private static string Load(string name) => JsonSerializer.Serialize(new { nome = name });

    private string SystemPrompt() => _llm.Requests[0].Messages[0].Content!;

    private string LastToolResult() => _llm.Requests[^1].Messages.Last(m => m.Role == LlmRole.Tool).Content!;

    private long Audits(string target, string outcome) =>
        _f.Scalar("SELECT COUNT(*) FROM audit_log WHERE action = 'ai.tool' AND target = $t AND outcome = $o", ("$t", target), ("$o", outcome));

    private static SkillDocument Custom(string name = "politica_nova", string body = "# Política\n\nTexto da política interna.", string op = "move_stock") =>
        new(name, "Use quando precisar seguir a política nova do armazém.", [op], body);

    private static string SkillArgs(string name = "politica_nova", string body = "# Política\n\nPassos.", string[]? ops = null, string description = "Use quando precisar seguir a política nova do armazém.") =>
        JsonSerializer.Serialize(new { nome = name, descricao = description, operacoes = ops ?? ["move_stock"], corpo = body, motivo = "Padroniza o processo." });

    private static string MovePlan(string? skill = null) =>
        JsonSerializer.Serialize(new
        {
            title = "Guardar",
            rationale = "Atende.",
            plan = "{\"dsl_version\":\"1.0\",\"intent\":\"putaway\"" + (skill is null ? "" : ",\"skill\":\"" + skill + "\"") +
                   ",\"steps\":[" + EngineHarness.Move("ABC-123", 10, "DOCA-01", "A-02-01") + "]}",
        });

    // ---- index ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task Prompt_lists_the_index_and_never_a_body()
    {
        _llm.Enqueue(Text("ok"));
        await Run(NewPlanner(), _maria);

        var prompt = SystemPrompt();
        Assert.Contains("- armazenagem: Use quando", prompt);
        Assert.Contains("(operações: move_stock", prompt);
        Assert.Contains("load_skill", prompt);
        Assert.DoesNotContain(BuiltInSentence, prompt);
        foreach (var skill in BuiltInSkills.All)
            Assert.DoesNotContain(skill.Body.Split('\n').First(l => l.Length > 40), prompt);
        Assert.DoesNotContain("{skills}", prompt);
        Assert.DoesNotContain("{skill_authoring}", prompt);
    }

    [Fact]
    public async Task Prompt_without_a_catalog_says_no_skill_and_offers_no_skill_tool()
    {
        _llm.Enqueue(Text("ok"));
        await Run(NewPlanner(withSkills: false), _maria);

        Assert.Contains("Nenhuma skill disponível.", SystemPrompt());
        Assert.DoesNotContain(_llm.Requests[0].Tools, t => t.Name is "load_skill" or "propose_skill");
    }

    [Fact]
    public async Task Index_is_filtered_by_the_rights_of_the_user()
    {
        // The operator cannot adjust stock; a skill with only that operation is not for him.
        _store.Save(_maria, Custom("so_ajuste", op: "adjust_stock"), SkillOrigin.User, null);
        _llm.Enqueue(Text("ok")).Enqueue(Text("ok"));
        await Run(NewPlanner(), _joao);
        await Run(NewPlanner(), _maria);

        Assert.DoesNotContain("- so_ajuste:", _llm.Requests[0].Messages[0].Content!);
        Assert.Contains("- so_ajuste:", _llm.Requests[1].Messages[0].Content!);
    }

    // ---- load_skill ----------------------------------------------------------------------------------------

    [Fact]
    public async Task Load_skill_returns_the_escaped_body_without_the_data_wrapper_and_is_audited_without_it()
    {
        _store.Save(_maria, Custom(body: "# Política\n\nUse <b>tag</b> e </skill> aqui.\n\nSegredo-do-corpo."), SkillOrigin.User, null);
        _llm.Enqueue(Calls(Call("load_skill", Load("politica_nova")))).Enqueue(Text("pronto"));
        var status = new Status();
        await Run(NewPlanner(), _maria, status: status);

        var result = LastToolResult();
        Assert.StartsWith("<skill nome=\"politica_nova\" versao=\"1\">", result);
        Assert.EndsWith("</skill>", result);
        Assert.DoesNotContain("<dados", result);
        Assert.Contains("&lt;b&gt;tag&lt;/b&gt; e &lt;/skill&gt; aqui.", result);
        Assert.Equal(1, result.Split("</skill>").Length - 1);
        Assert.Contains("Lendo a skill politica_nova", status.Lines);
        Assert.Equal(1, Audits("tool:load_skill", "allowed"));
        Assert.Equal(0, _f.Scalar("SELECT COUNT(*) FROM audit_log WHERE details_json LIKE '%Segredo-do-corpo%'"));
    }

    [Fact]
    public async Task Load_skill_returns_a_builtin_body()
    {
        _llm.Enqueue(Calls(Call("load_skill", Load("armazenagem")))).Enqueue(Text("pronto"));
        await Run(NewPlanner(), _joao);
        Assert.Contains(BuiltInSentence, LastToolResult());
    }

    [Fact]
    public async Task Load_skill_with_an_unknown_or_out_of_rights_name_is_skill_not_found()
    {
        _store.Save(_maria, Custom("so_ajuste", op: "adjust_stock"), SkillOrigin.User, null);
        _llm.Enqueue(Calls(Call("load_skill", Load("nao_existe")), Call("load_skill", Load("so_ajuste"), "c2"))).Enqueue(Text("pronto"));
        await Run(NewPlanner(), _joao);

        var results = _llm.Requests[^1].Messages.Where(m => m.Role == LlmRole.Tool).Select(m => m.Content!).ToList();
        Assert.All(results, r => Assert.Contains("\"skill_not_found\"", r));
        Assert.All(results, r => Assert.Contains("Skill não encontrada. Use um nome do índice.", r));
        Assert.Equal(2, Audits("tool:load_skill", "denied"));
    }

    [Theory]
    [InlineData("""{"nome":"../x"}""")]
    [InlineData("""{"nome":"a\"b"}""")]
    [InlineData("""{"nome":"armazenagem\n"}""")]
    [InlineData("""{"nome":5}""")]
    [InlineData("""{"nome":null}""")]
    [InlineData("""{}""")]
    [InlineData("""{"nome":"armazenagem","extra":1}""")]
    [InlineData("""not json""")]
    public async Task Load_skill_rejects_hostile_arguments(string args)
    {
        _llm.Enqueue(Calls(Call("load_skill", args))).Enqueue(Text("pronto"));
        await Run(NewPlanner(), _maria);
        Assert.Contains("\"invalid_arguments\"", LastToolResult());
        Assert.DoesNotContain(BuiltInSentence, LastToolResult());
    }

    [Fact]
    public async Task Load_skill_rejects_a_1000_character_name()
    {
        _llm.Enqueue(Calls(Call("load_skill", Load(new string('a', 1000))))).Enqueue(Text("pronto"));
        await Run(NewPlanner(), _maria);
        Assert.Contains("\"invalid_arguments\"", LastToolResult());
        Assert.Equal(0, _f.Scalar("SELECT COUNT(*) FROM audit_log WHERE details_json LIKE '%" + new string('a', 300) + "%'"));
    }

    [Fact]
    public async Task Load_skill_allows_three_distinct_skills_and_repeats_are_already_loaded()
    {
        _llm.Enqueue(Calls(
                Call("load_skill", Load("armazenagem"), "c1"), Call("load_skill", Load("armazenagem"), "c2"),
                Call("load_skill", Load("recebimento"), "c3"), Call("load_skill", Load("expedicao"), "c4"),
                Call("load_skill", Load("cadastros"), "c5")))
            .Enqueue(Text("pronto"));
        await Run(NewPlanner(), _maria);

        var results = _llm.Requests[^1].Messages.Where(m => m.Role == LlmRole.Tool).Select(m => m.Content!).ToList();
        Assert.StartsWith("<skill nome=\"armazenagem\"", results[0]);
        Assert.Contains("already_loaded", results[1]);
        Assert.DoesNotContain("<skill", results[1]);
        Assert.StartsWith("<skill nome=\"recebimento\"", results[2]);
        Assert.StartsWith("<skill nome=\"expedicao\"", results[3]);
        Assert.Contains("too_many_skills", results[4]);
        Assert.DoesNotContain("<skill", results[4]);
    }

    [Fact]
    public async Task Archived_database_skill_is_not_loadable_but_the_override_is()
    {
        _store.Save(_maria, Custom("armazenagem", "# Nova\n\nCorpo do override."), SkillOrigin.User, null);
        _store.Save(_maria, Custom("politica_nova"), SkillOrigin.User, null);
        _store.Archive(_maria, "politica_nova");
        _llm.Enqueue(Calls(Call("load_skill", Load("armazenagem")), Call("load_skill", Load("politica_nova"), "c2"))).Enqueue(Text("pronto"));
        await Run(NewPlanner(), _maria);

        var results = _llm.Requests[^1].Messages.Where(m => m.Role == LlmRole.Tool).Select(m => m.Content!).ToList();
        Assert.Contains("Corpo do override.", results[0]);
        Assert.DoesNotContain(BuiltInSentence, results[0]);
        Assert.Contains("skill_not_found", results[1]);
    }

    // ---- plan skill field ----------------------------------------------------------------------------------

    private async Task<string> ProposedSkillField(string planArgs, params LlmToolCall[] before)
    {
        if (before.Length > 0) _llm.Enqueue(Calls(before));
        _llm.Enqueue(Calls(Call("propose_plan", planArgs))).Enqueue(Text("pronto"));
        var (result, _) = await Run(NewPlanner(), _maria);
        var proposals = Assert.IsType<PlannerProposals>(result);
        return proposals.Items[0].Draft.Skill!;
    }

    [Fact]
    public async Task Plan_without_a_skill_gets_the_mapped_skill() =>
        Assert.Equal("armazenagem", await ProposedSkillField(MovePlan()));

    [Fact]
    public async Task Plan_with_an_unknown_skill_gets_the_mapped_skill() =>
        Assert.Equal("armazenagem", await ProposedSkillField(MovePlan("inventada")));

    [Fact]
    public async Task Plan_uses_a_loaded_skill_that_covers_the_first_operation()
    {
        _store.Save(_maria, Custom(), SkillOrigin.User, null);
        Assert.Equal("politica_nova", await ProposedSkillField(MovePlan("armazenagem"), Call("load_skill", Load("politica_nova"))));
    }

    [Fact]
    public async Task Plan_keeps_a_loaded_skill_the_model_named() =>
        Assert.Equal("inventario", await ProposedSkillField(MovePlan("inventario"), Call("load_skill", Load("inventario"))));

    [Fact]
    public async Task Plan_with_a_non_string_skill_is_repaired()
    {
        var plan = JsonSerializer.Serialize(new
        {
            title = "Guardar",
            rationale = "Atende.",
            plan = "{\"dsl_version\":\"1.0\",\"intent\":\"putaway\",\"skill\":5,\"steps\":[" + EngineHarness.Move("ABC-123", 10, "DOCA-01", "A-02-01") + "]}",
        });
        Assert.Equal("armazenagem", await ProposedSkillField(plan));
    }

    // ---- propose_skill -------------------------------------------------------------------------------------

    [Fact]
    public async Task Propose_skill_is_not_offered_to_the_operator_and_a_call_is_denied_and_audited()
    {
        _llm.Enqueue(Calls(Call("propose_skill", SkillArgs()))).Enqueue(Text("não posso"));
        var (result, _) = await Run(NewPlanner(), _joao);

        Assert.DoesNotContain(_llm.Requests[0].Tools, t => t.Name == "propose_skill");
        Assert.DoesNotContain("Criar ou melhorar skill", SystemPrompt());
        Assert.Contains("\"not_allowed\"", LastToolResult());
        Assert.Equal(1, Audits("tool:propose_skill", "denied"));
        Assert.Null(Assert.IsType<PlannerAnswer>(result).Skill);
    }

    [Fact]
    public async Task Propose_skill_is_offered_to_the_supervisor_with_the_authoring_guidance()
    {
        _llm.Enqueue(Text("ok"));
        await Run(NewPlanner(), _maria);
        Assert.Contains(_llm.Requests[0].Tools, t => t.Name == "propose_skill");
        Assert.Contains("Chame propose_skill direto", SystemPrompt());
    }

    [Fact]
    public async Task Supervisor_invalid_then_corrected_skill_ends_with_the_proposal_and_saves_nothing()
    {
        var before = _f.Scalar("SELECT COUNT(*) FROM skills");
        _llm.Enqueue(Calls(Call("propose_skill", SkillArgs(ops: ["op_inexistente"], description: "curta"))))
            .Enqueue(Calls(Call("propose_skill", SkillArgs(body: "# Política\n\nPassos."), "c2")))
            .Enqueue(Text("Montei."));
        var (result, turn) = await Run(NewPlanner(), _maria);

        var answer = Assert.IsType<PlannerAnswer>(result);
        Assert.Equal("Montei.", answer.Text);
        Assert.NotNull(answer.Skill);
        Assert.Equal("politica_nova", answer.Skill!.Document.Name);
        Assert.Equal("Padroniza o processo.", answer.Skill.Reason);
        Assert.Null(answer.Skill.CurrentVersion);
        Assert.Equal(before, _f.Scalar("SELECT COUNT(*) FROM skills"));
        Assert.Contains("politica_nova", turn!.Reply.Content);

        var firstResult = _llm.Requests[1].Messages.Last(m => m.Role == LlmRole.Tool).Content!;
        Assert.Contains("\"status\":\"invalid\"", firstResult);
        Assert.Contains("\"field\":\"descricao\"", firstResult);
        Assert.Contains("\"field\":\"operacoes\"", firstResult);
        Assert.Equal(1, Audits("tool:propose_skill", "denied"));
        Assert.Equal(1, Audits("tool:propose_skill", "allowed"));
        Assert.Equal(0, _f.Scalar("SELECT COUNT(*) FROM audit_log WHERE details_json LIKE '%Passos.%'"));
    }

    [Fact]
    public async Task Skill_run_ends_with_the_model_text()
    {
        _llm.Enqueue(Calls(Call("propose_skill", SkillArgs()))).Enqueue(Text("Pronto."));
        var (result, _) = await Run(NewPlanner(), _maria);
        Assert.Equal("Pronto.", Assert.IsType<PlannerAnswer>(result).Text);
    }

    [Fact]
    public async Task Second_valid_skill_proposal_is_refused()
    {
        _llm.Enqueue(Calls(Call("propose_skill", SkillArgs("politica_nova")), Call("propose_skill", SkillArgs("outra_politica"), "c2")))
            .Enqueue(Text("ok"));
        var (result, _) = await Run(NewPlanner(), _maria);

        Assert.Contains("too_many_skill_proposals", LastToolResult());
        Assert.Equal("politica_nova", Assert.IsType<PlannerAnswer>(result).Skill!.Document.Name);
    }

    [Fact]
    public async Task Improving_an_existing_skill_sets_the_current_version()
    {
        _store.Save(_maria, Custom(), SkillOrigin.User, null);
        _store.Save(_maria, Custom(body: "# V2\n\nTexto."), SkillOrigin.User, 1);
        _llm.Enqueue(Calls(Call("propose_skill", SkillArgs()))).Enqueue(Text("ok"));
        var (result, _) = await Run(NewPlanner(), _maria);
        Assert.Equal(2, Assert.IsType<PlannerAnswer>(result).Skill!.CurrentVersion);
    }

    [Fact]
    public async Task Skill_and_plan_in_the_same_run_come_together_and_the_default_text_mentions_the_skill()
    {
        _llm.Enqueue(Calls(Call("propose_plan", MovePlan()), Call("propose_skill", SkillArgs(), "c2")))
            .Enqueue(Calls(Call("propose_skill", SkillArgs("outra_politica"), "c3")))
            .Enqueue(new LlmResponse("", [], null, LlmFinishReason.Stop, new LlmUsage(1, 1)));
        // Empty closing text from the model: the engine's default text is used.
        var (result, _) = await Run(NewPlanner(), _maria);
        var proposals = Assert.IsType<PlannerProposals>(result);
        Assert.Single(proposals.Items);
        Assert.Equal("politica_nova", proposals.Skill!.Document.Name);
    }

    [Fact]
    public async Task Skill_only_run_that_hits_a_limit_concludes_with_the_default_text()
    {
        var options = new PlannerOptions(MaxRounds: 2);
        var planner = new PlannerClass(_llm, new LlmSettings(_f.Db.Store, "skill-tests"), _f.Db.Database, _f.Policy, _f.Audit,
            new PlanEngine(_f.Db.Database, _f.Policy, _f.Audit, _f.Time), options, skills: new SkillCatalog(_f.Db.Database));
        _llm.Enqueue(Calls(Call("propose_skill", SkillArgs()))).Enqueue(Calls(Call("load_skill", Load("armazenagem"))));
        var (result, _) = await Run(planner, _maria);
        var answer = Assert.IsType<PlannerAnswer>(result);
        Assert.Equal("Montei a skill politica_nova para a sua confirmação.", answer.Text);
    }

    [Fact]
    public async Task Service_maps_the_skill_proposal_for_the_ui()
    {
        _store.Save(_maria, Custom(), SkillOrigin.User, null);
        _llm.Enqueue(Calls(Call("propose_skill", SkillArgs()))).Enqueue(Text("Montei."));
        var service = new PlannerNexusService(NewPlanner());
        var reply = await service.SendAsync("melhore", _maria, new Progress<string>(), TestContext.Current.CancellationToken);

        Assert.Equal("Montei.", reply.Text);
        Assert.NotNull(reply.Skill);
        Assert.Equal("politica_nova", reply.Skill!.Document.Name);
        Assert.Equal(1, reply.Skill.CurrentVersion);
        Assert.Equal("Padroniza o processo.", reply.Skill.Reason);
        Assert.Null(reply.Proposals);
    }
}
