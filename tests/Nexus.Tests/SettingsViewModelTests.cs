using erp_inteligente.Core.Llm;
using erp_inteligente.Core.Policy;
using erp_inteligente.ViewModels;

namespace Nexus.Tests;

/// <summary>The LLM provider section of Configurações over the real core: only the administrator manages the key.</summary>
public sealed class SettingsViewModelTests : IDisposable
{
    private const string Key = "sk-test-0123456789abcdef";

    private readonly TestApp _app = new();

    public void Dispose() => _app.Dispose();

    private async Task<SettingsViewModel> SettingsFor(string username) =>
        new(await _app.LoginAsync(username), "banco de teste", _app.Host.LlmSettingsService);

    private long Count(string sql)
    {
        using var connection = _app.Host.Database.OpenApplication();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return (long)command.ExecuteScalar()!;
    }

    [Fact]
    public async Task Administrator_saves_the_key_in_the_key_store_and_the_field_is_cleared()
    {
        var vm = await SettingsFor("adm.carlos");
        Assert.True(vm.CanConfigure);
        Assert.False(vm.IsKeyConfigured);

        vm.ApiKey = Key;
        await vm.SaveKeyCommand.ExecuteAsync(null);

        Assert.Equal("", vm.ApiKey);
        Assert.True(vm.IsKeyConfigured);
        Assert.True(vm.FeedbackIsSuccess);
        Assert.Contains("Chave configurada", vm.KeyStatusText);
        var stored = _app.Host.LlmSettings.ReadKey()!;
        Assert.Equal(Key, System.Text.Encoding.UTF8.GetString(stored));
        Assert.Equal(1, Count("SELECT COUNT(*) FROM audit_log WHERE action = 'llm.key.set'"));
        Assert.Equal(0, Count($"SELECT COUNT(*) FROM audit_log WHERE COALESCE(details_json, '') LIKE '%{Key}%'"));
    }

    [Fact]
    public async Task Malformed_key_is_refused_without_echoing_it()
    {
        var vm = await SettingsFor("adm.carlos");
        vm.ApiKey = "chave com espaço";

        await vm.SaveKeyCommand.ExecuteAsync(null);

        Assert.True(vm.FeedbackIsError);
        Assert.DoesNotContain("espaço", vm.Feedback.Replace("sem espaços", ""));
        Assert.False(vm.IsKeyConfigured);
    }

    [Fact]
    public async Task Administrator_removes_the_key_and_changes_the_model()
    {
        var vm = await SettingsFor("adm.carlos");
        vm.ApiKey = Key;
        await vm.SaveKeyCommand.ExecuteAsync(null);

        await vm.ClearKeyCommand.ExecuteAsync(null);
        Assert.False(vm.IsKeyConfigured);
        Assert.Null(_app.Host.LlmSettings.ReadKey());

        vm.SelectedModel = LlmSettings.FastModel;
        for (var i = 0; i < 50 && vm.IsWorking; i++) await Task.Delay(10, TestContext.Current.CancellationToken);
        Assert.Equal(LlmSettings.FastModel, _app.Host.LlmSettings.Model);
        Assert.Equal(1, Count("SELECT COUNT(*) FROM audit_log WHERE action = 'llm.key.clear'"));
        Assert.Equal(1, Count("SELECT COUNT(*) FROM audit_log WHERE action = 'llm.model.set'"));
    }

    [Theory]
    [InlineData("op.joao")]
    [InlineData("sup.maria")]
    public async Task Other_roles_only_see_the_key_state(string username)
    {
        var vm = await SettingsFor(username);
        vm.ApiKey = Key;

        Assert.False(vm.CanConfigure);
        Assert.True(vm.IsReadOnlyLlm);
        Assert.False(vm.SaveKeyCommand.CanExecute(null));
        Assert.Contains("peça ao administrador", vm.KeyStatusText);
    }

    [Fact]
    public async Task Administrator_keeps_a_key_per_provider_and_picks_a_model_per_role()
    {
        var vm = await SettingsFor("adm.carlos");
        Assert.Equal(["DeepSeek", "Claude (Anthropic)", "OpenAI"], vm.ProviderRows.Select(r => r.Name));

        vm.SelectedKeyProvider = vm.KeyProviders.Single(p => p.Kind == LlmProviderKind.Claude);
        vm.ApiKey = "sk-ant-test-0123456789";
        await vm.SaveKeyCommand.ExecuteAsync(null);
        Assert.True(_app.Host.LlmSettings.IsConfiguredFor(LlmProviderKind.Claude));
        Assert.False(_app.Host.LlmSettings.IsConfigured); // DeepSeek's key is another entry
        Assert.Equal(1, Count("SELECT COUNT(*) FROM audit_log WHERE action = 'llm.key.set' AND target = 'llm:claude'"));

        vm.PlannerProvider = vm.PlannerProviders.Single(p => p.Kind == LlmProviderKind.Claude);
        for (var i = 0; i < 50 && vm.IsWorking; i++) await Task.Delay(10, TestContext.Current.CancellationToken);
        Assert.Equal(LlmSettings.ModelsFor(LlmProviderKind.Claude), vm.PlannerModels);
        Assert.Equal(new LlmChoice(LlmProviderKind.Claude, LlmSettings.ClaudeStrongModel), _app.Host.LlmSettings.ChoiceFor(LlmUseRole.Planner));
        Assert.Contains("usada em planejamento", vm.ProviderRows.Single(r => r.Name == "Claude (Anthropic)").StatusText);

        vm.MatcherProvider = vm.MatcherProviders.Single(p => p.Kind == LlmProviderKind.Local);
        for (var i = 0; i < 50 && vm.IsWorking; i++) await Task.Delay(10, TestContext.Current.CancellationToken);
        Assert.Equal(new LlmChoice(LlmProviderKind.Local, LlmSettings.LocalModel), _app.Host.LlmSettings.ChoiceFor(LlmUseRole.Matcher));
        Assert.DoesNotContain(vm.PlannerProviders, p => p.Kind == LlmProviderKind.Local); // the local model never plans

        vm.LocalPortText = "80";
        await vm.SaveLocalPortCommand.ExecuteAsync(null);
        Assert.True(vm.FeedbackIsError);
        vm.LocalPortText = "8099";
        await vm.SaveLocalPortCommand.ExecuteAsync(null);
        Assert.Equal(8099, _app.Host.LlmSettings.LocalPort);
        Assert.Equal(2, Count("SELECT COUNT(*) FROM audit_log WHERE action = 'llm.model.set'"));
        Assert.Equal(0, Count("SELECT COUNT(*) FROM audit_log WHERE COALESCE(details_json, '') LIKE '%sk-ant-test%'"));
    }

    [Fact]
    public async Task Other_roles_cannot_change_the_model_of_a_role()
    {
        var vm = await SettingsFor("sup.maria");
        vm.PlannerProvider = vm.PlannerProviders.Single(p => p.Kind == LlmProviderKind.Claude);
        await Task.Delay(50, TestContext.Current.CancellationToken);
        Assert.Equal(LlmProviderKind.DeepSeek, _app.Host.LlmSettings.ChoiceFor(LlmUseRole.Planner).Provider);
        var maria = await _app.LoginAsync("sup.maria");
        Assert.Throws<PolicyDeniedException>(() => _app.Host.LlmSettingsService.SetChoice(maria, LlmUseRole.Planner,
            new LlmChoice(LlmProviderKind.Claude, LlmSettings.ClaudeStrongModel)));
    }

    [Fact]
    public async Task Service_refuses_and_audits_a_non_administrator_even_without_the_screen()
    {
        var joao = await _app.LoginAsync("op.joao");

        Assert.Throws<PolicyDeniedException>(() => _app.Host.LlmSettingsService.SaveKey(joao, Key));

        Assert.False(_app.Host.LlmSettings.IsConfigured);
        Assert.Equal(1, Count("SELECT COUNT(*) FROM audit_log WHERE action = 'policy.denied' AND target = 'permission:user.manage'"));
    }
}
