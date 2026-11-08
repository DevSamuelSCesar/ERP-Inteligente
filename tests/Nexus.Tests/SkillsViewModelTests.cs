using erp_inteligente.Core.Skills;
using erp_inteligente.Services;
using erp_inteligente.Services.Sqlite;
using erp_inteligente.ViewModels;

namespace Nexus.Tests;

/// <summary>Skills screen and the skill drafted by the Nexus, over the real store (TestApp).</summary>
public class SkillsViewModelTests
{
    private const string Body = "# Devoluções\n\n## Passos\n1. Confira o produto devolvido e guarde na doca.\n";

    private static async Task<(SkillsViewModel Page, ISkillService Service)> PageFor(TestApp app, string username)
    {
        var user = await app.LoginAsync(username);
        var service = new SqliteSkillService(app.Host.SkillStore, app.Host.Session);
        var page = new SkillsViewModel(user, service);
        await page.OnActivatedAsync();
        return (page, service);
    }

    private static void Fill(SkillsViewModel page, string name, params string[] ops)
    {
        page.EditName = name;
        page.EditDescription = "Use quando um cliente devolver mercadoria ao armazém.";
        page.EditBody = Body;
        foreach (var choice in page.Choices)
            choice.IsSelected = ops.Contains(choice.Name);
    }

    [Fact]
    public async Task Operator_reads_the_built_in_skills_without_any_write_action()
    {
        using var app = new TestApp();
        var (page, _) = await PageFor(app, "op.joao");

        Assert.True(page.IsReadOnly);
        Assert.Equal(["armazenagem", "cadastros", "expedicao", "inventario", "recebimento", "separacao"], page.Rows.Select(r => r.Name));
        Assert.All(page.Rows, r => Assert.Equal("Embutida", r.OriginText));
        Assert.NotNull(page.SelectedRow);
        Assert.False(page.NewSkillCommand.CanExecute(null));
        Assert.False(page.PersonalizeCommand.CanExecute(null));
        Assert.False(page.CanPersonalizeSelected);
        Assert.NotEmpty(page.SelectedOperations);
    }

    [Fact]
    public async Task Auditor_is_read_only_too()
    {
        using var app = new TestApp();
        var (page, _) = await PageFor(app, "aud.lucia");

        Assert.True(page.IsReadOnly);
        Assert.NotEmpty(page.Rows);
        Assert.False(page.NewSkillCommand.CanExecute(null));
    }

    [Fact]
    public async Task Supervisor_creates_and_edits_a_skill_with_new_versions()
    {
        using var app = new TestApp();
        var (page, _) = await PageFor(app, "sup.maria");
        Assert.False(page.IsReadOnly);

        page.NewSkillCommand.Execute(null);
        Assert.True(page.IsEditing);
        Fill(page, "devolucoes", "move_stock");
        Assert.True(page.IsEditorValid, page.NameError + page.DescriptionError + page.OperationsError + page.BodyError);
        await page.SaveCommand.ExecuteAsync(null);

        Assert.False(page.IsEditing);
        Assert.Equal("devolucoes", page.SelectedRow!.Name);
        Assert.Equal("v1", page.SelectedRow.VersionText);
        Assert.Equal("Cadastrada", page.SelectedRow.OriginText);
        Assert.True(page.FeedbackIsSuccess);

        page.EditCommand.Execute(null);
        Assert.False(page.IsNameEditable);
        page.EditBody = Body + "2. Registre o motivo da devolução.\n";
        await page.SaveCommand.ExecuteAsync(null);

        Assert.Equal("v2", page.SelectedRow!.VersionText);
        Assert.Equal(2, app.Host.SkillStore.History(app.Host.Session.Current, "devolucoes").Count);
    }

    [Fact]
    public async Task Invalid_editor_blocks_save_with_messages_per_field()
    {
        using var app = new TestApp();
        var (page, _) = await PageFor(app, "sup.maria");
        page.NewSkillCommand.Execute(null);

        Fill(page, "Devoluções");
        page.EditDescription = "curta";
        page.EditBody = "```json\n{\"dsl_version\":\"1.0\"\n```";

        Assert.False(page.IsEditorValid);
        Assert.NotNull(page.NameError);
        Assert.NotNull(page.DescriptionError);
        Assert.NotNull(page.BodyError);
        Assert.False(page.SaveCommand.CanExecute(null));
    }

    [Fact]
    public async Task A_name_already_in_the_database_is_refused_in_the_editor()
    {
        using var app = new TestApp();
        var (page, _) = await PageFor(app, "sup.maria");
        page.NewSkillCommand.Execute(null);
        Fill(page, "devolucoes");
        await page.SaveCommand.ExecuteAsync(null);

        page.NewSkillCommand.Execute(null);
        Fill(page, "devolucoes");

        Assert.False(page.IsEditorValid);
        Assert.Contains("Já existe", page.NameError);
    }

    [Fact]
    public async Task Personalizing_a_built_in_replaces_it_until_archived()
    {
        using var app = new TestApp();
        var (page, _) = await PageFor(app, "sup.maria");
        page.SelectedRow = page.Rows.Single(r => r.Name == "recebimento");
        Assert.False(page.EditCommand.CanExecute(null));
        Assert.False(page.ArchiveCommand.CanExecute(null));

        page.PersonalizeCommand.Execute(null);
        Assert.Equal("recebimento", page.EditName);
        page.EditBody += "\n## Regra do armazém\n- Conferência cega em todo recebimento.\n";
        await page.SaveCommand.ExecuteAsync(null);

        var custom = page.SelectedRow!;
        Assert.Equal("recebimento", custom.Name);
        Assert.Equal("Personalizada", custom.OriginText);
        Assert.Contains("Conferência cega", custom.Record.Body);

        await page.ArchiveCommand.ExecuteAsync(null);
        Assert.Equal("Embutida", page.Rows.Single(r => r.Name == "recebimento").OriginText);
        Assert.Contains("embutida voltou", page.Feedback);

        page.ShowArchived = true;
        var archived = Assert.Single(page.Rows);
        Assert.Equal("recebimento", archived.Name);
        await page.ReactivateCommand.ExecuteAsync(null);
        Assert.Empty(page.Rows);
    }

    [Fact]
    public async Task Cancel_leaves_the_editor_without_saving()
    {
        using var app = new TestApp();
        var (page, service) = await PageFor(app, "sup.maria");
        page.NewSkillCommand.Execute(null);
        Fill(page, "devolucoes");

        page.CancelEditCommand.Execute(null);

        Assert.False(page.IsEditing);
        Assert.DoesNotContain(await service.ListAsync(), s => s.Name == "devolucoes");
    }

    [Fact]
    public async Task Filter_matches_name_and_description()
    {
        using var app = new TestApp();
        var (page, _) = await PageFor(app, "op.joao");

        page.FilterText = "doca";

        Assert.NotEmpty(page.Rows);
        Assert.All(page.Rows, r => Assert.True(
            r.Name.Contains("doca", StringComparison.OrdinalIgnoreCase) || r.Description.Contains("doca", StringComparison.OrdinalIgnoreCase)));
    }

    // ---- skill drafted by the Nexus ------------------------------------------------------------------------

    private static NexusSkillProposal Draft(int? currentVersion = null) => new(
        new SkillDocument("devolucoes", "Use quando um cliente devolver mercadoria ao armazém.", ["move_stock"], Body),
        "Você pediu um procedimento de devolução.", currentVersion);

    [Fact]
    public async Task Confirming_a_drafted_skill_saves_it_as_created_by_the_ai()
    {
        using var app = new TestApp();
        var (_, service) = await PageFor(app, "sup.maria");
        var card = new SkillProposalViewModel(Draft(), service);
        Assert.Equal("Skill proposta · devolucoes", card.Title);
        Assert.DoesNotContain(await service.ListAsync(), s => s.Name == "devolucoes");

        await card.ConfirmCommand.ExecuteAsync(null);

        Assert.True(card.IsDone);
        var saved = Assert.Single(await service.ListAsync(), s => s.Name == "devolucoes");
        Assert.Equal(SkillOrigin.Ai, saved.Origin);
        Assert.Equal("Criada pela IA", SkillsViewModel.OriginText(saved));
    }

    [Fact]
    public async Task Discarding_a_drafted_skill_saves_nothing()
    {
        using var app = new TestApp();
        var (_, service) = await PageFor(app, "sup.maria");
        var card = new SkillProposalViewModel(Draft(), service);

        card.DiscardCommand.Execute(null);

        Assert.Equal("Descartada. Nada foi gravado.", card.StatusText);
        Assert.False(card.ConfirmCommand.CanExecute(null));
        Assert.DoesNotContain(await service.ListAsync(), s => s.Name == "devolucoes");
    }

    [Fact]
    public async Task A_drafted_skill_confirmed_by_someone_without_the_right_is_not_saved()
    {
        using var app = new TestApp();
        var (_, service) = await PageFor(app, "op.joao");
        var card = new SkillProposalViewModel(Draft(), service);

        await card.ConfirmCommand.ExecuteAsync(null);

        Assert.True(card.IsFailed);
        Assert.Contains("não permite", card.StatusText);
    }

    [Fact]
    public async Task A_new_version_drafted_over_a_stale_one_is_a_conflict()
    {
        using var app = new TestApp();
        var (_, service) = await PageFor(app, "sup.maria");
        await service.SaveAsync(Draft().Document, SkillOrigin.User, null);
        await service.SaveAsync(Draft().Document with { Body = Body + "2. Outro passo.\n" }, SkillOrigin.User, 1);

        var card = new SkillProposalViewModel(Draft(currentVersion: 1), service);
        Assert.Equal("Nova versão de devolucoes (substitui a v1)", card.Title);
        await card.ConfirmCommand.ExecuteAsync(null);

        Assert.True(card.IsFailed);
        Assert.Contains("alterada por outra pessoa", card.StatusText);
    }

    [Fact]
    public async Task Nexus_shows_a_drafted_skill_and_locks_the_request_until_decided()
    {
        var args = System.Text.Json.JsonSerializer.Serialize(new
        {
            nome = "devolucoes",
            descricao = "Use quando um cliente devolver mercadoria ao armazém.",
            operacoes = new[] { "move_stock" },
            corpo = Body,
            motivo = "Você pediu um procedimento de devolução.",
        });
        var llm = new Fakes.ScriptedLlmProvider().Call("propose_skill", args).Reply("Montei a skill devolucoes.");
        using var app = new TestApp(llm: llm);
        var shell = await app.ShellForAsync("sup.maria");
        var nexus = Assert.IsType<NexusViewModel>(shell.CurrentPage);

        nexus.Draft = "crie uma skill de devolução";
        await nexus.SendCommand.ExecuteAsync(null);

        var card = Assert.IsType<SkillProposalViewModel>(nexus.Entries[^1].Body);
        Assert.Same(card, nexus.PendingSkill);
        Assert.False(nexus.IsInputEnabled);

        await card.ConfirmCommand.ExecuteAsync(null);

        Assert.Null(nexus.PendingSkill);
        Assert.True(nexus.IsInputEnabled);
        var skills = await new SqliteSkillService(app.Host.SkillStore, app.Host.Session).ListAsync();
        Assert.Equal(SkillOrigin.Ai, Assert.Single(skills, s => s.Name == "devolucoes").Origin);
    }
}
