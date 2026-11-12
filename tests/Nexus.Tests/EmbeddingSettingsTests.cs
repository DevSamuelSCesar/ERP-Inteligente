using System.Net;
using System.Text;
using System.Text.Json;
using erp_inteligente.Core.Llm;
using erp_inteligente.ViewModels;

namespace Nexus.Tests;

/// <summary>Feature 5 completed: the "Busca por significado" line of Configurações over the real core and a fake embeddings server.</summary>
public sealed class EmbeddingSettingsTests : IDisposable
{
    /// <summary>Answers /v1/embeddings with one 3-dimension vector per input; counts the texts it embedded.</summary>
    private sealed class FakeEmbeddingsServer : HttpMessageHandler
    {
        public int Texts { get; private set; }
        public HttpStatusCode Status { get; set; } = HttpStatusCode.OK;
        public List<Uri> Urls { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Urls.Add(request.RequestUri!);
            if (Status != HttpStatusCode.OK) return new HttpResponseMessage(Status);
            using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(ct));
            var inputs = body.RootElement.GetProperty("input").GetArrayLength();
            Texts += inputs;
            var data = string.Join(",", Enumerable.Range(0, inputs).Select(i => $$"""{"index":{{i}},"embedding":[{{i + 1}},1,0.5]}"""));
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent($$$"""{"data":[{{{data}}}],"usage":{"prompt_tokens":{{{inputs * 10}}}}}""", Encoding.UTF8, "application/json"),
            };
        }
    }

    private readonly FakeEmbeddingsServer _server = new();
    private readonly TestApp _app;

    public EmbeddingSettingsTests() => _app = new TestApp(embeddingsHandler: _server);

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

    private static async Task Settle(SettingsViewModel vm)
    {
        for (var i = 0; i < 200 && vm.IsWorking; i++) await Task.Delay(10);
        await Task.Delay(20);
        for (var i = 0; i < 200 && vm.IsWorking; i++) await Task.Delay(10);
    }

    [Fact]
    public async Task Off_by_default_and_choosing_the_local_model_indexes_every_chunk()
    {
        var vm = await SettingsFor("adm.carlos");
        Assert.True(vm.HasEmbeddings);
        Assert.Null(vm.SelectedEmbedding!.Choice);
        Assert.Contains("Desligada", vm.EmbeddingStatusText);
        Assert.False(vm.IndexCommand.CanExecute(null));

        vm.SelectedEmbedding = vm.EmbeddingOptions.Single(o => o.Choice?.Provider == LlmProviderKind.Local);
        await Settle(vm);

        var chunks = Count("SELECT count(*) FROM knowledge_chunks");
        Assert.True(vm.FeedbackIsSuccess, vm.Feedback);
        Assert.Equal(chunks, Count("SELECT count(*) FROM knowledge_embeddings WHERE model = 'local:nexus-embed'"));
        Assert.Equal(chunks, _server.Texts);
        Assert.All(_server.Urls, u => Assert.Equal("127.0.0.1", u.Host));
        Assert.Contains($"{chunks} de {chunks} trechos com vetor", vm.EmbeddingStatusText);
        Assert.Equal(1, Count("SELECT count(*) FROM audit_log WHERE action = 'llm.embedding.set' AND details_json LIKE '%\"local\"%'"));

        // Indexing again sends nothing: every chunk already has its vector.
        await vm.IndexCommand.ExecuteAsync(null);
        Assert.Equal(chunks, _server.Texts);
    }

    [Fact]
    public async Task A_failing_provider_is_reported_and_the_search_keeps_working()
    {
        _server.Status = HttpStatusCode.ServiceUnavailable;
        var vm = await SettingsFor("adm.carlos");
        vm.SelectedEmbedding = vm.EmbeddingOptions.Single(o => o.Choice?.Provider == LlmProviderKind.Local);
        await Settle(vm);

        Assert.True(vm.FeedbackIsError);
        Assert.Contains("servidor local", vm.Feedback);
        Assert.Equal(0, Count("SELECT count(*) FROM knowledge_embeddings"));
        Assert.NotEmpty(await _app.Host.KnowledgeRetriever.SearchHybridAsync("armazenagem de refrigerado na câmara",
            cancellationToken: TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Only_the_administrator_changes_the_embeddings()
    {
        var vm = await SettingsFor("sup.maria");
        Assert.False(vm.CanConfigure);
        vm.SelectedEmbedding = vm.EmbeddingOptions.Single(o => o.Choice?.Provider == LlmProviderKind.OpenAI);
        await Settle(vm);
        Assert.Null(_app.Host.LlmSettings.EmbeddingChoice);
        Assert.Equal(0, _server.Texts);
        Assert.False(vm.IndexCommand.CanExecute(null));
    }
}
