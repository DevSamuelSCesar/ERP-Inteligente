using System.Text.Json;
using erp_inteligente.Core.Engine;
using erp_inteligente.Core.Llm;
using erp_inteligente.Core.Planner;
using erp_inteligente.Models;
using Nexus.Tests.Core.Engine;
using Nexus.Tests.Core.Policy;
using PlannerClass = erp_inteligente.Core.Planner.Planner;

namespace Nexus.Tests.Core.Planner;

/// <summary>propose_plan (several validated alternatives, nothing written) and offer_options through the Planner loop.</summary>
public sealed class ProposePlanTests : IDisposable
{
    private readonly PolicyTestFixture _f = new();
    private readonly ScriptedLlm _llm = new();
    private readonly UserSession _joao;
    private readonly UserSession _maria;

    public ProposePlanTests()
    {
        _joao = _f.SessionFor("op.joao");
        _maria = _f.SessionFor("sup.maria");
    }

    public void Dispose() => _f.Dispose();

    private PlannerClass NewPlanner(PlannerOptions? options = null) =>
        new(_llm, new LlmSettings(_f.Db.Store, "propose-tests"), _f.Db.Database, _f.Policy, _f.Audit,
            new PlanEngine(_f.Db.Database, _f.Policy, _f.Audit, _f.Time), options, _f.Time);

    private Task<(PlannerResult Result, PlannerTurn? Turn)> Run(UserSession user, string request = "pedido", PlannerOptions? options = null) =>
        NewPlanner(options).RunAsync(user, [], request, new Progress<string>(), TestContext.Current.CancellationToken);

    private static LlmResponse Text(string text) => new(text, [], null, LlmFinishReason.Stop, new LlmUsage(10, 10));

    private static LlmResponse Calls(params LlmToolCall[] calls) => new(null, calls, null, LlmFinishReason.ToolCalls, new LlmUsage(10, 10));

    private static LlmToolCall Call(string name, string args, string id = "c1") => new(id, name, args);

    private static string Plan(string title, params string[] steps) =>
        JsonSerializer.Serialize(new
        {
            title,
            rationale = "Porque sim.",
            assumptions = new[] { "capacidade 100" },
            plan = "{\"dsl_version\":\"1.0\",\"intent\":\"putaway\",\"steps\":[" + string.Join(",", steps) + "]}",
        });

    private static string Move(string to, int qty = 10) => EngineHarness.Move("ABC-123", qty, "DOCA-01", to);

    private string ToolResults(int request) =>
        string.Join("\n", _llm.Requests[request].Messages.Where(m => m.Role == LlmRole.Tool).Select(m => m.Content));

    private (long Plans, long Zones, long Locations, long Stock) Snapshot() =>
        (_f.Scalar("SELECT COUNT(*) FROM plans"), _f.Scalar("SELECT COUNT(*) FROM zones"),
         _f.Scalar("SELECT COUNT(*) FROM locations"), _f.Scalar("SELECT SUM(quantity) FROM stock_balances"));

    // ---- propose_plan -------------------------------------------------------------------------------------

    [Fact]
    public async Task Chained_plan_creating_a_zone_and_locations_before_moving_is_one_valid_proposal_and_writes_nothing()
    {
        var before = Snapshot();
        var steps = new[]
        {
            """{"op":"create_zone","args":{"code":"Q","name":"Quarentena"}}""",
            """{"op":"create_location_range","args":{"zone":"Q","aisle":1,"level_from":1,"level_to":3,"capacity":100}}""",
            Move("Q-01-01"),
        };
        _llm.Enqueue(Calls(Call("propose_plan", Plan("Quarentena e guarda", steps)))).Enqueue(Text("Criei a zona Q e movi."));
        var (result, _) = await Run(_maria);

        var proposals = Assert.IsType<PlannerProposals>(result);
        var item = Assert.Single(proposals.Items);
        Assert.Empty(item.Preview.Errors);
        Assert.Equal(3, item.Preview.Document!.Steps.Count);
        Assert.Contains("create_location_range", item.Draft.Json);
        Assert.Equal(["capacidade 100"], item.Assumptions);
        Assert.Equal(before, Snapshot());
    }

    [Fact]
    public async Task Three_alternatives_end_the_loop_with_one_final_round_without_tools()
    {
        _llm.Enqueue(Calls(Call("propose_plan", Plan("A", Move("A-02-01")), "1")))
            .Enqueue(Calls(Call("propose_plan", Plan("B", Move("A-02-02")), "2")))
            .Enqueue(Calls(Call("propose_plan", Plan("C", Move("A-02-03")), "3")))
            .Enqueue(Text("Três opções."));
        var (result, turn) = await Run(_joao);

        var proposals = Assert.IsType<PlannerProposals>(result);
        Assert.Equal(["A", "B", "C"], proposals.Items.Select(i => i.Title));
        Assert.Equal("Três opções.", proposals.Text);
        Assert.Equal(4, _llm.Requests.Count);
        Assert.Empty(_llm.Requests[3].Tools);
        Assert.Contains("Planos: A; B; C", turn!.Reply.Content);
    }

    [Fact]
    public async Task Third_proposal_without_a_closing_text_gets_a_default_text()
    {
        _llm.Enqueue(Calls(Call("propose_plan", Plan("A", Move("A-02-01")), "1"), Call("propose_plan", Plan("B", Move("A-02-02")), "2"),
                Call("propose_plan", Plan("C", Move("A-02-03")), "3")))
            .Throw(new LlmException(LlmErrorKind.Unavailable, "down"));
        var (result, _) = await Run(_joao);

        var proposals = Assert.IsType<PlannerProposals>(result);
        Assert.Equal(3, proposals.Items.Count);
        Assert.Contains("3 alternativas", proposals.Text);
    }

    [Fact]
    public async Task A_fourth_proposal_in_the_same_round_is_refused_and_not_counted_as_invalid()
    {
        _llm.Enqueue(Calls(
                Call("propose_plan", Plan("A", Move("A-02-01")), "1"), Call("propose_plan", Plan("B", Move("A-02-02")), "2"),
                Call("propose_plan", Plan("C", Move("A-02-03")), "3"), Call("propose_plan", Plan("D", Move("A-02-04")), "4")))
            .Enqueue(Text("fim"));
        var (result, _) = await Run(_joao);

        Assert.Equal(3, Assert.IsType<PlannerProposals>(result).Items.Count);
        Assert.Contains("too_many_proposals", ToolResults(1));
    }

    [Fact]
    public async Task The_same_plan_twice_is_refused_as_a_duplicate()
    {
        _llm.Enqueue(Calls(Call("propose_plan", Plan("A", Move("A-02-01")), "1")))
            .Enqueue(Calls(Call("propose_plan", Plan("A de novo", Move("A-02-01")), "2")))
            .Enqueue(Text("ok"));
        var (result, _) = await Run(_joao);

        Assert.Single(Assert.IsType<PlannerProposals>(result).Items);
        Assert.Contains("duplicate_plan", ToolResults(2));
    }

    [Fact]
    public async Task Four_invalid_plans_after_a_valid_one_still_return_the_valid_proposal()
    {
        _llm.Enqueue(Calls(Call("propose_plan", Plan("Bom", Move("A-02-01")), "0")));
        for (var i = 1; i <= 4; i++)
            _llm.Enqueue(Calls(Call("propose_plan", Plan("Ruim", Move("Q-99-99", i)), "b" + i)));
        var (result, _) = await Run(_joao);

        Assert.Equal("Bom", Assert.Single(Assert.IsType<PlannerProposals>(result).Items).Title);
    }

    [Fact]
    public async Task Invalid_argument_shapes_are_reported_and_count_as_attempts()
    {
        var longTitle = new string('t', 81);
        _llm.Enqueue(Calls(
                Call("propose_plan", Plan(longTitle, Move("A-02-01")), "a"),
                Call("propose_plan", "{\"title\":\"x\",\"rationale\":\"\",\"plan\":{}}", "b"),
                Call("propose_plan", "{\"title\":\"x\",\"rationale\":\"y\"}", "c"),
                Call("propose_plan", "{\"title\":\"x\",\"rationale\":\"y\",\"assumptions\":[1],\"plan\":{}}", "d"),
                Call("propose_plan", "[1]", "e"),
                Call("propose_plan", "{\"title\":\"x\",\"rationale\":\"y\",\"assumptions\":[" + string.Join(",", Enumerable.Repeat("\"s\"", 9)) + "],\"plan\":{}}", "f")))
            .Enqueue(Calls(Call("propose_plan", Plan("x", Move("A-02-01")), "g")))
            .Enqueue(Text("ok"));
        var (result, _) = await Run(_joao, options: new PlannerOptions(MaxInvalidPlans: 9));

        var tools = _llm.Requests[1].Messages.Where(m => m.Role == LlmRole.Tool).Select(m => m.Content!).ToList();
        Assert.Equal(6, tools.Count);
        Assert.All(tools, t => Assert.Contains("invalid_arguments", t));
        Assert.Contains("title", tools[0]);
        Assert.Contains("rationale", tools[1]);
        Assert.Contains("plan", tools[2]);
        Assert.Contains("assumptions", tools[3]);
        Assert.Contains("assumptions", tools[5]);
        Assert.Single(Assert.IsType<PlannerProposals>(result).Items);
    }

    [Fact]
    public async Task Texts_from_the_model_lose_control_characters()
    {
        var args = JsonSerializer.Serialize(new
        {
            title = "Gu\u0007ardar\r\n‮depressa",
            rationale = "linha1\nlinha2\t​fim",
            assumptions = new[] { "a\u0000b" },
            plan = "{\"dsl_version\":\"1.0\",\"intent\":\"x\",\"steps\":[" + Move("A-02-01") + "]}",
        });
        _llm.Enqueue(Calls(Call("propose_plan", args))).Enqueue(Text("ok"));
        var (result, _) = await Run(_joao);

        var item = Assert.Single(Assert.IsType<PlannerProposals>(result).Items);
        Assert.Equal("Gu ardar depressa", item.Title);
        Assert.Equal("linha1 linha2 fim", item.Rationale);
        Assert.Equal(["a b"], item.Assumptions);
    }

    [Fact]
    public async Task Operator_proposing_create_location_gets_policy_denied_feedback()
    {
        var plan = Plan("Novo endereço", """{"op":"create_location","args":{"code":"A-03-01","zone":"A","capacity":60}}""");
        _llm.Enqueue(Calls(Call("propose_plan", plan))).Enqueue(Text("Você não pode cadastrar endereços; peça a um supervisor."));
        var (result, _) = await Run(_joao);

        Assert.IsType<PlannerAnswer>(result);
        var feedback = ToolResults(1);
        Assert.Contains("\"status\":\"invalid\"", feedback);
        Assert.Contains("policy.denied", feedback);
    }

    [Fact]
    public async Task Proposals_and_a_valid_run_are_audited()
    {
        _llm.Enqueue(Calls(Call("propose_plan", Plan("A", Move("A-02-01"))))).Enqueue(Text("ok"));
        await Run(_joao);

        Assert.Equal(1, _f.Scalar("SELECT COUNT(*) FROM audit_log WHERE action = 'ai.tool' AND target = 'tool:propose_plan' AND outcome = 'allowed'"));
    }

    [Fact]
    public async Task Max_rounds_with_proposals_returns_them_instead_of_escalating()
    {
        _llm.Enqueue(Calls(Call("propose_plan", Plan("A", Move("A-02-01")), "1")))
            .Enqueue(Calls(Call("get_product", "{\"sku\":\"ABC-123\"}", "2")))
            .Enqueue(Calls(Call("get_product", "{\"sku\":\"ABC-123\"}", "3")));
        var (result, _) = await Run(_joao, options: new PlannerOptions(MaxRounds: 3));

        Assert.Single(Assert.IsType<PlannerProposals>(result).Items);
    }

    [Fact]
    public void Defaults_follow_the_product_owner_decision()
    {
        var options = PlannerOptions.Default;
        Assert.Equal((12, 4, 3), (options.MaxRounds, options.MaxInvalidPlans, options.MaxProposals));
        Assert.Equal(60000, options.MaxTokensPerRequest);
    }

    // ---- offer_options ------------------------------------------------------------------------------------

    [Fact]
    public async Task Options_pass_through_with_the_answer_and_are_sanitized()
    {
        var args = JsonSerializer.Serialize(new
        {
            options = new[]
            {
                new { label = "Zona\u0007 Q\n(quarentena)", request = "Crie a zona Q\r\ne mova 10 do ABC-123" },
                new { label = "Zona R", request = "Crie a zona R" },
            },
        });
        _llm.Enqueue(Calls(Call("offer_options", args))).Enqueue(Text("Qual zona?"));
        var (result, turn) = await Run(_joao);

        var answer = Assert.IsType<PlannerAnswer>(result);
        Assert.Equal("Qual zona?", answer.Text);
        Assert.Equal([new PlannerOption("Zona Q (quarentena)", "Crie a zona Q e mova 10 do ABC-123"), new PlannerOption("Zona R", "Crie a zona R")], answer.Options);
        Assert.Contains("\"status\":\"ok\"", ToolResults(1));
        Assert.NotNull(turn);
    }

    [Fact]
    public async Task Options_come_with_the_proposals_and_the_last_call_wins()
    {
        _llm.Enqueue(Calls(Call("offer_options", """{"options":[{"label":"velho","request":"velho"}]}""", "1"),
                Call("propose_plan", Plan("A", Move("A-02-01")), "2"),
                Call("offer_options", """{"options":[{"label":"Ajustar","request":"Ajuste o estoque"}]}""", "3")))
            .Enqueue(Text("Aqui está."));
        var (result, _) = await Run(_joao);

        var proposals = Assert.IsType<PlannerProposals>(result);
        Assert.Equal([new PlannerOption("Ajustar", "Ajuste o estoque")], proposals.Options);
    }

    [Fact]
    public async Task Options_without_text_get_a_default_question()
    {
        _llm.Enqueue(Calls(Call("offer_options", """{"options":[{"label":"Sim","request":"sim"}]}"""))).Enqueue(Text("   "));
        var (result, _) = await Run(_joao);

        var answer = Assert.IsType<PlannerAnswer>(result);
        Assert.Equal("Como prefere seguir?", answer.Text);
        Assert.Single(answer.Options);
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("""{"options":[]}""")]
    [InlineData("""{"options":"x"}""")]
    [InlineData("""{"options":[{"label":"a"}]}""")]
    [InlineData("""{"options":[{"label":"","request":"x"}]}""")]
    [InlineData("""{"options":["texto"]}""")]
    [InlineData("""{"options":[{"label":"a","request":"b"},{"label":"a","request":"b"},{"label":"a","request":"b"},{"label":"a","request":"b"},{"label":"a","request":"b"}]}""")]
    [InlineData("[]")]
    [InlineData("{not json")]
    public async Task Invalid_options_are_refused(string args)
    {
        _llm.Enqueue(Calls(Call("offer_options", args))).Enqueue(Text("sem opções"));
        var (result, _) = await Run(_joao);

        Assert.Contains("invalid_arguments", ToolResults(1));
        Assert.Empty(Assert.IsType<PlannerAnswer>(result).Options);
    }

    [Fact]
    public async Task Too_long_labels_and_requests_are_refused()
    {
        _llm.Enqueue(Calls(Call("offer_options", JsonSerializer.Serialize(new { options = new[] { new { label = new string('l', 61), request = "x" } } }), "1"),
                Call("offer_options", JsonSerializer.Serialize(new { options = new[] { new { label = "x", request = new string('r', 301) } } }), "2")))
            .Enqueue(Text("ok"));
        var (result, _) = await Run(_joao);

        Assert.Empty(Assert.IsType<PlannerAnswer>(result).Options);
        Assert.Equal(2, ToolResults(1).Split("invalid_arguments").Length - 1);
    }

    [Fact]
    public async Task Read_only_role_can_still_offer_options()
    {
        _llm.Enqueue(Calls(Call("offer_options", """{"options":[{"label":"Consultar","request":"saldo do ABC-123"}]}"""))).Enqueue(Text("ok"));
        var (result, _) = await Run(_f.SessionFor("aud.lucia"));

        Assert.Single(Assert.IsType<PlannerAnswer>(result).Options);
    }

    [Fact]
    public async Task The_system_prompt_carries_today_the_lot_pattern_and_the_proactive_rules()
    {
        _llm.Enqueue(Text("ok"));
        await Run(_joao);

        var system = _llm.Requests[0].Messages[0].Content!;
        Assert.Contains("2026-11-07", system);
        Assert.Contains("20261107-<SKU>", system);
        Assert.DoesNotContain("{today", system);
        Assert.Contains("propose_plan", system);
        Assert.Contains("offer_options", system);
        Assert.Contains("next_free_codes", system);
        Assert.Contains("<dados>", system);
        Assert.DoesNotContain("submit_plan", system);
    }
}
