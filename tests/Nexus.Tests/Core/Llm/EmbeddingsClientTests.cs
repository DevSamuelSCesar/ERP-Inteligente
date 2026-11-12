using System.Net;
using System.Text;
using System.Text.Json;
using erp_inteligente.Core.Llm;
using erp_inteligente.Core.Security;

namespace Nexus.Tests.Core.Llm;

/// <summary>Feature 5 completed: the embeddings client (OpenAI or 127.0.0.1) and the embeddings choice in the settings.</summary>
public sealed class EmbeddingsClientTests
{
    private const string Key = "sk-test-EMBED-1234567890";

    private sealed class FakeHandler(Func<HttpRequestMessage, int, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public List<(HttpRequestMessage Request, string Body)> Calls { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(ct);
            Calls.Add((request, body));
            return respond(request, Calls.Count);
        }
    }

    private static HttpResponseMessage Json(HttpStatusCode code, string body) =>
        new(code) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    private const string TwoVectors =
        """{"object":"list","data":[{"object":"embedding","index":1,"embedding":[0.0,1.0,0.5]},{"object":"embedding","index":0,"embedding":[1.0,0.0,-0.5]}],"usage":{"prompt_tokens":7,"total_tokens":7}}""";

    private static Task NoDelay(TimeSpan _, CancellationToken __) => Task.CompletedTask;

    private static (ConfiguredEmbeddingProvider Provider, LlmSettings Settings, FakeHandler Handler) Make(
        Func<HttpRequestMessage, int, HttpResponseMessage> respond)
    {
        var settings = new LlmSettings(new InMemorySecretStore(), "db-embeddings");
        var handler = new FakeHandler(respond);
        return (new ConfiguredEmbeddingProvider(settings, handler, NoDelay), settings, handler);
    }

    [Fact]
    public async Task OpenAi_sends_the_documented_body_with_the_openai_key_and_reads_vectors_by_index()
    {
        var (provider, settings, handler) = Make((_, _) => Json(HttpStatusCode.OK, TwoVectors));
        settings.SetKey(LlmProviderKind.OpenAI, Key);
        settings.EmbeddingChoice = new LlmChoice(LlmProviderKind.OpenAI, LlmSettings.OpenAiEmbeddingModel);

        var batch = await provider.EmbedAsync(["primeiro", "segundo"], TestContext.Current.CancellationToken);

        Assert.Equal("openai:text-embedding-3-small", provider.Model);
        Assert.Equal([1f, 0f, -0.5f], batch.Vectors[0]);
        Assert.Equal([0f, 1f, 0.5f], batch.Vectors[1]);
        Assert.Equal(7, batch.Tokens);
        var (request, body) = Assert.Single(handler.Calls);
        Assert.Equal("https://api.openai.com/v1/embeddings", request.RequestUri!.ToString());
        Assert.Equal("Bearer " + Key, request.Headers.Authorization!.ToString());
        using var json = JsonDocument.Parse(body);
        Assert.Equal("text-embedding-3-small", json.RootElement.GetProperty("model").GetString());
        Assert.Equal(["primeiro", "segundo"], json.RootElement.GetProperty("input").EnumerateArray().Select(e => e.GetString()));
        Assert.Equal("float", json.RootElement.GetProperty("encoding_format").GetString());
    }

    [Fact]
    public async Task Local_goes_to_loopback_without_a_key()
    {
        var (provider, settings, handler) = Make((_, _) => Json(HttpStatusCode.OK, TwoVectors));
        settings.LocalPort = 8099;
        settings.EmbeddingChoice = new LlmChoice(LlmProviderKind.Local, LlmSettings.LocalEmbeddingModel);

        await provider.EmbedAsync(["a", "b"], TestContext.Current.CancellationToken);

        var (request, body) = Assert.Single(handler.Calls);
        Assert.Equal("http://127.0.0.1:8099/v1/embeddings", request.RequestUri!.ToString());
        Assert.Null(request.Headers.Authorization);
        Assert.Contains("\"model\":\"nexus-embed\"", body);
        Assert.Equal("local:nexus-embed", provider.Model);
    }

    [Fact]
    public async Task Off_or_without_a_key_nothing_is_sent()
    {
        var (provider, settings, handler) = Make((_, _) => Json(HttpStatusCode.OK, TwoVectors));
        Assert.Null(provider.Model);
        Assert.Equal(LlmErrorKind.NotConfigured,
            (await Assert.ThrowsAsync<LlmException>(() => provider.EmbedAsync(["a"], CancellationToken.None))).Kind);

        settings.EmbeddingChoice = new LlmChoice(LlmProviderKind.OpenAI, LlmSettings.OpenAiEmbeddingModel);
        Assert.Equal(LlmErrorKind.NotConfigured,
            (await Assert.ThrowsAsync<LlmException>(() => provider.EmbedAsync(["a"], CancellationToken.None))).Kind);
        Assert.Empty(handler.Calls);
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized, LlmErrorKind.Unauthorized, 1)]
    [InlineData(HttpStatusCode.TooManyRequests, LlmErrorKind.RateLimited, 3)]
    [InlineData(HttpStatusCode.BadGateway, LlmErrorKind.Unavailable, 3)]
    [InlineData(HttpStatusCode.BadRequest, LlmErrorKind.InvalidResponse, 1)]
    public async Task Errors_carry_only_a_kind_and_retries_are_bounded(HttpStatusCode status, LlmErrorKind kind, int calls)
    {
        var (provider, settings, handler) = Make((_, _) => Json(status, """{"error":{"message":"bad key sk-test-EMBED"}}"""));
        settings.SetKey(LlmProviderKind.OpenAI, Key);
        settings.EmbeddingChoice = new LlmChoice(LlmProviderKind.OpenAI, LlmSettings.OpenAiEmbeddingModel);

        var error = await Assert.ThrowsAsync<LlmException>(() => provider.EmbedAsync(["a"], CancellationToken.None));
        Assert.Equal(kind, error.Kind);
        Assert.DoesNotContain("sk-test", error.Message);
        Assert.Equal(calls, handler.Calls.Count);
    }

    [Theory]
    [InlineData("""{"data":[]}""")]
    [InlineData("""{"data":[{"index":0,"embedding":[1,2]},{"index":0,"embedding":[1,2]}]}""")]
    [InlineData("""{"data":[{"index":0,"embedding":[1,2]},{"index":1,"embedding":[1]}]}""")]
    [InlineData("""{"data":[{"index":0,"embedding":["x"]},{"index":1,"embedding":[1]}]}""")]
    [InlineData("""not json""")]
    public void Malformed_responses_are_rejected(string json) =>
        Assert.Equal(LlmErrorKind.InvalidResponse, Assert.Throws<LlmException>(() => EmbeddingsClient.Parse(json, 2)).Kind);

    [Fact]
    public async Task Endpoint_other_than_openai_or_loopback_is_refused()
    {
        var handler = new FakeHandler((_, _) => Json(HttpStatusCode.OK, TwoVectors));
        var client = new EmbeddingsClient(handler, () => new Uri("https://evil.example.com/v1/embeddings"), () => Encoding.UTF8.GetBytes(Key));
        Assert.Equal(LlmErrorKind.NotConfigured,
            (await Assert.ThrowsAsync<LlmException>(() => client.EmbedAsync("m", ["a"], CancellationToken.None))).Kind);
        Assert.Empty(handler.Calls);
    }

    [Fact]
    public void Settings_accept_only_the_embeddings_model_of_each_provider()
    {
        var settings = new LlmSettings(new InMemorySecretStore(), "db-x");
        Assert.Null(settings.EmbeddingChoice);
        Assert.Throws<ArgumentException>(() => settings.EmbeddingChoice = new LlmChoice(LlmProviderKind.DeepSeek, "deepseek-flash"));
        Assert.Throws<ArgumentException>(() => settings.EmbeddingChoice = new LlmChoice(LlmProviderKind.OpenAI, "gpt-5"));
        settings.EmbeddingChoice = new LlmChoice(LlmProviderKind.Local, LlmSettings.LocalEmbeddingModel);
        Assert.Equal(LlmProviderKind.Local, settings.EmbeddingChoice!.Provider);
        settings.EmbeddingChoice = null;
        Assert.Null(settings.EmbeddingChoice);
    }
}
