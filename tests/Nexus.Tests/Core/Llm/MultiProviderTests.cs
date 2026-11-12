using System.Net;
using System.Text;
using System.Text.Json;
using erp_inteligente.Core.Llm;
using erp_inteligente.Core.Security;

namespace Nexus.Tests.Core.Llm;

/// <summary>Feature 8: Claude (official SDK), OpenAI and the local model adapters, the router and the model per role.</summary>
public sealed class MultiProviderTests
{
    private const string Key = "sk-test-MULTI-1234567890";

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

    private static readonly LlmTool Tool = new("get_stock", "Saldo por SKU.",
        """{"type":"object","additionalProperties":false,"required":["sku"],"properties":{"sku":{"type":"string","pattern":"^[A-Z]{3}-[0-9]{3}$"}}}""");

    // ---- Claude ---------------------------------------------------------------------------------------------

    private const string ClaudeToolUse = """
        {"id":"msg_1","type":"message","role":"assistant","model":"claude-sonnet-5-5",
         "content":[{"type":"thinking","thinking":"","signature":"sig-abc"},
                    {"type":"text","text":"Vou consultar."},
                    {"type":"tool_use","id":"toolu_1","name":"get_stock","input":{"sku":"ABC-123"}}],
         "stop_reason":"tool_use","stop_sequence":null,
         "usage":{"input_tokens":120,"output_tokens":30}}
        """;

    private const string ClaudeText = """
        {"id":"msg_2","type":"message","role":"assistant","model":"claude-sonnet-5-5",
         "content":[{"type":"text","text":"ABC-123 tem 80 caixas."}],
         "stop_reason":"end_turn","stop_sequence":null,"usage":{"input_tokens":200,"output_tokens":12}}
        """;

    [Fact]
    public async Task Claude_sends_the_messages_api_shape_and_maps_tool_calls_back()
    {
        var handler = new FakeHandler((_, n) => Json(HttpStatusCode.OK, n == 1 ? ClaudeToolUse : ClaudeText));
        var claude = new ClaudeProvider(handler, () => Encoding.UTF8.GetBytes(Key));
        var ct = TestContext.Current.CancellationToken;

        var first = await claude.CompleteAsync(new LlmRequest("claude-sonnet-5-5",
            [LlmMessage.System("Você é o Nexus."), LlmMessage.User("saldo do ABC-123")], [Tool], 4096), ct);

        Assert.Equal(LlmFinishReason.ToolCalls, first.FinishReason);
        Assert.Equal("Vou consultar.", first.Content);
        var call = Assert.Single(first.ToolCalls);
        Assert.Equal(("toolu_1", "get_stock"), (call.Id, call.Name));
        Assert.Equal("ABC-123", JsonDocument.Parse(call.ArgumentsJson).RootElement.GetProperty("sku").GetString());
        Assert.Equal(new LlmUsage(120, 30), first.Usage);

        var (request, body) = handler.Calls[0];
        Assert.Equal("https://api.anthropic.com/v1/messages", request.RequestUri!.ToString());
        // The SDK decides how the key travels (x-api-key or Authorization); either way it is the key from the store.
        var auth = request.Headers.TryGetValues("x-api-key", out var apiKey) ? apiKey.Single()
            : request.Headers.Authorization?.ToString() ?? "";
        Assert.Contains(Key, auth);
        Assert.True(request.Headers.Contains("anthropic-version"));
        using (var doc = JsonDocument.Parse(body))
        {
            var root = doc.RootElement;
            Assert.Equal("claude-sonnet-5-5", root.GetProperty("model").GetString());
            Assert.Equal(4096, root.GetProperty("max_tokens").GetInt32());
            Assert.Equal("Você é o Nexus.", root.GetProperty("system").ValueKind == JsonValueKind.String
                ? root.GetProperty("system").GetString() : root.GetProperty("system")[0].GetProperty("text").GetString());
            Assert.Equal("user", root.GetProperty("messages")[0].GetProperty("role").GetString());
            var tool = root.GetProperty("tools")[0];
            Assert.Equal("get_stock", tool.GetProperty("name").GetString());
            Assert.Equal("^[A-Z]{3}-[0-9]{3}$", tool.GetProperty("input_schema").GetProperty("properties").GetProperty("sku").GetProperty("pattern").GetString());
            Assert.False(root.TryGetProperty("temperature", out _));
        }

        // The tool loop: the assistant turn goes back with its signed thinking block; two tool results become one user message.
        var second = await claude.CompleteAsync(new LlmRequest("claude-sonnet-5-5",
        [
            LlmMessage.System("Você é o Nexus."), LlmMessage.User("saldo do ABC-123"), LlmMessage.Assistant(first),
            LlmMessage.ToolResult("toolu_1", "<dados>80</dados>"), LlmMessage.ToolResult("toolu_2", "<dados>0</dados>"),
        ], [Tool], 4096), ct);
        Assert.Equal(LlmFinishReason.Stop, second.FinishReason);
        Assert.Equal("ABC-123 tem 80 caixas.", second.Content);

        using var followUp = JsonDocument.Parse(handler.Calls[1].Body);
        var messages = followUp.RootElement.GetProperty("messages");
        Assert.Equal(3, messages.GetArrayLength());
        var assistant = messages[1].GetProperty("content");
        Assert.Equal("thinking", assistant[0].GetProperty("type").GetString());
        Assert.Equal("sig-abc", assistant[0].GetProperty("signature").GetString());
        Assert.Equal("tool_use", assistant[2].GetProperty("type").GetString());
        var results = messages[2].GetProperty("content");
        Assert.Equal(2, results.GetArrayLength());
        Assert.All(results.EnumerateArray(), r => Assert.Equal("tool_result", r.GetProperty("type").GetString()));
        Assert.Equal("toolu_2", results[1].GetProperty("tool_use_id").GetString());
    }

    [Fact]
    public async Task Claude_rebuilds_a_turn_from_another_provider()
    {
        var handler = new FakeHandler((_, _) => Json(HttpStatusCode.OK, ClaudeText));
        var claude = new ClaudeProvider(handler, () => Encoding.UTF8.GetBytes(Key));
        var calls = new[] { new LlmToolCall("c1", "get_stock", """{"sku":"ABC-123"}""") };

        await claude.CompleteAsync(new LlmRequest("claude-haiku-5-5",
            [LlmMessage.User("oi"), LlmMessage.Assistant(null, calls), LlmMessage.ToolResult("c1", "80")], [Tool], 100),
            TestContext.Current.CancellationToken);

        using var doc = JsonDocument.Parse(handler.Calls[0].Body);
        var use = doc.RootElement.GetProperty("messages")[1].GetProperty("content")[0];
        Assert.Equal("tool_use", use.GetProperty("type").GetString());
        Assert.Equal("ABC-123", use.GetProperty("input").GetProperty("sku").GetString());
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized, LlmErrorKind.Unauthorized)]
    [InlineData(HttpStatusCode.Forbidden, LlmErrorKind.Unauthorized)]
    [InlineData(HttpStatusCode.TooManyRequests, LlmErrorKind.RateLimited)]
    [InlineData((HttpStatusCode)529, LlmErrorKind.Unavailable)]
    [InlineData(HttpStatusCode.BadRequest, LlmErrorKind.InvalidResponse)]
    public async Task Claude_errors_become_generic_llm_errors_without_the_key(HttpStatusCode status, LlmErrorKind kind)
    {
        var handler = new FakeHandler((_, _) => Json(status,
            """{"type":"error","error":{"type":"x","message":"echo sk-test-MULTI-1234567890"}}"""));
        var claude = new ClaudeProvider(handler, () => Encoding.UTF8.GetBytes(Key), TimeSpan.FromSeconds(5), maxRetries: 0);

        var e = await Assert.ThrowsAsync<LlmException>(() => claude.CompleteAsync(
            new LlmRequest("claude-haiku-5-5", [LlmMessage.User("oi")], [], 10), TestContext.Current.CancellationToken));
        Assert.Equal(kind, e.Kind);
        Assert.DoesNotContain(Key, e.Message);
    }

    [Fact]
    public async Task Claude_marks_the_stable_prefix_for_prompt_caching_and_counts_cached_tokens()
    {
        const string cached = """
            {"id":"msg_3","type":"message","role":"assistant","model":"claude-sonnet-5-5",
             "content":[{"type":"text","text":"ok"}],"stop_reason":"end_turn","stop_sequence":null,
             "usage":{"input_tokens":40,"output_tokens":5,"cache_creation_input_tokens":300,"cache_read_input_tokens":2000}}
            """;
        var handler = new FakeHandler((_, _) => Json(HttpStatusCode.OK, cached));
        var claude = new ClaudeProvider(handler, () => Encoding.UTF8.GetBytes(Key));
        var ct = TestContext.Current.CancellationToken;

        var response = await claude.CompleteAsync(new LlmRequest("claude-sonnet-5-5",
            [LlmMessage.System("Você é o Nexus."), LlmMessage.User("oi")], [Tool], 1024), ct);

        // Prompt total = uncached input + cache write + cache read.
        Assert.Equal(new LlmUsage(2340, 5) { CacheReadTokens = 2000, CacheWriteTokens = 300 }, response.Usage);
        using (var doc = JsonDocument.Parse(handler.Calls[0].Body))
        {
            var root = doc.RootElement;
            Assert.Equal("ephemeral", root.GetProperty("system")[0].GetProperty("cache_control").GetProperty("type").GetString());
            Assert.Equal("ephemeral", root.GetProperty("cache_control").GetProperty("type").GetString()); // the conversation
            Assert.False(root.GetProperty("tools")[0].TryGetProperty("cache_control", out _)); // covered by the system breakpoint
        }

        // Without a system prompt the breakpoint goes on the last tool definition.
        await claude.CompleteAsync(new LlmRequest("claude-haiku-5-5", [LlmMessage.User("oi")], [Tool], 256), ct);
        using var noSystem = JsonDocument.Parse(handler.Calls[1].Body);
        Assert.Equal("ephemeral", noSystem.RootElement.GetProperty("tools")[0].GetProperty("cache_control").GetProperty("type").GetString());
    }

    [Fact]
    public async Task Claude_without_a_key_never_calls_the_network()
    {
        var handler = new FakeHandler((_, _) => Json(HttpStatusCode.OK, ClaudeText));
        var claude = new ClaudeProvider(handler, () => null);
        var e = await Assert.ThrowsAsync<LlmException>(() => claude.CompleteAsync(
            new LlmRequest("claude-haiku-5-5", [LlmMessage.User("oi")], [], 10), TestContext.Current.CancellationToken));
        Assert.Equal(LlmErrorKind.NotConfigured, e.Kind);
        Assert.Empty(handler.Calls);
    }

    // ---- OpenAI and local ----------------------------------------------------------------------------------

    private const string ChatOk = """{"choices":[{"index":0,"message":{"role":"assistant","content":"ok"},"finish_reason":"stop"}],"usage":{"prompt_tokens":3,"completion_tokens":1}}""";

    [Fact]
    public async Task OpenAi_uses_its_fixed_host_and_max_completion_tokens()
    {
        var handler = new FakeHandler((_, _) => Json(HttpStatusCode.OK, ChatOk));
        var openAi = new OpenAiProvider(handler, () => Encoding.UTF8.GetBytes(Key), (_, _) => Task.CompletedTask);

        var response = await openAi.CompleteAsync(new LlmRequest("gpt-5",
            [LlmMessage.User("oi"), LlmMessage.Assistant("pensei", null, "raciocínio")], [Tool], 500), TestContext.Current.CancellationToken);

        Assert.Equal("ok", response.Content);
        var (request, body) = handler.Calls[0];
        Assert.Equal("https://api.openai.com/v1/chat/completions", request.RequestUri!.ToString());
        Assert.Equal("Bearer", request.Headers.Authorization!.Scheme);
        using var doc = JsonDocument.Parse(body);
        Assert.Equal(500, doc.RootElement.GetProperty("max_completion_tokens").GetInt32());
        Assert.False(doc.RootElement.TryGetProperty("max_tokens", out _));
        Assert.False(doc.RootElement.GetProperty("messages")[1].TryGetProperty("reasoning_content", out _)); // DeepSeek only
    }

    [Fact]
    public async Task Local_model_talks_only_to_loopback_without_a_key()
    {
        var handler = new FakeHandler((_, _) => Json(HttpStatusCode.OK, ChatOk));
        var local = new LocalProvider(handler, () => 8091);
        await local.CompleteAsync(new LlmRequest(LlmSettings.LocalModel, [LlmMessage.User("oi")], [], 50), TestContext.Current.CancellationToken);

        var request = handler.Calls[0].Request;
        Assert.Equal("http://127.0.0.1:8091/v1/chat/completions", request.RequestUri!.ToString());
        Assert.Null(request.Headers.Authorization);

        var badPort = new LocalProvider(handler, () => 80);
        var e = await Assert.ThrowsAsync<LlmException>(() => badPort.CompleteAsync(
            new LlmRequest(LlmSettings.LocalModel, [LlmMessage.User("oi")], [], 5), TestContext.Current.CancellationToken));
        Assert.Equal(LlmErrorKind.NotConfigured, e.Kind);
    }

    // ---- router and settings -------------------------------------------------------------------------------

    private sealed class Recorder(string name) : ILlmProvider
    {
        public List<LlmRequest> Seen { get; } = [];

        public Task<LlmResponse> CompleteAsync(LlmRequest request, CancellationToken cancellationToken)
        {
            Seen.Add(request);
            return Task.FromResult(new LlmResponse(name, [], null, LlmFinishReason.Stop, new LlmUsage(0, 0)));
        }
    }

    [Fact]
    public async Task Router_sends_each_request_to_its_provider_and_defaults_to_deepseek()
    {
        var deepSeek = new Recorder("deepseek");
        var claude = new Recorder("claude");
        var router = new LlmRouter(new Dictionary<LlmProviderKind, ILlmProvider>
        {
            [LlmProviderKind.DeepSeek] = deepSeek,
            [LlmProviderKind.Claude] = claude,
        });
        var ct = TestContext.Current.CancellationToken;

        Assert.Equal("claude", (await router.CompleteAsync(new LlmRequest("m", [], [], 1, Provider: LlmProviderKind.Claude), ct)).Content);
        Assert.Equal("deepseek", (await router.CompleteAsync(new LlmRequest("m", [], [], 1), ct)).Content);
        var e = await Assert.ThrowsAsync<LlmException>(() => router.CompleteAsync(new LlmRequest("m", [], [], 1, Provider: LlmProviderKind.OpenAI), ct));
        Assert.Equal(LlmErrorKind.NotConfigured, e.Kind);
    }

    [Fact]
    public void Settings_keep_one_key_per_provider_and_one_model_per_role()
    {
        var store = new InMemorySecretStore();
        var settings = new LlmSettings(store, "db1");

        Assert.Equal(new LlmChoice(LlmProviderKind.DeepSeek, LlmSettings.StrongModel), settings.ChoiceFor(LlmUseRole.Planner));
        Assert.Equal(new LlmChoice(LlmProviderKind.DeepSeek, LlmSettings.FastModel), settings.ChoiceFor(LlmUseRole.Matcher));

        settings.SetKey(LlmProviderKind.Claude, "sk-ant-test-12345678");
        Assert.True(settings.IsConfiguredFor(LlmProviderKind.Claude));
        Assert.False(settings.IsConfiguredFor(LlmProviderKind.OpenAI));
        Assert.False(settings.IsConfigured); // DeepSeek's
        Assert.NotNull(store.Get("db1", "llm.claude.key"));

        settings.SetChoice(LlmUseRole.Planner, new LlmChoice(LlmProviderKind.Claude, LlmSettings.ClaudeStrongModel));
        settings.SetChoice(LlmUseRole.Matcher, new LlmChoice(LlmProviderKind.Local, LlmSettings.LocalModel));
        Assert.Equal(LlmProviderKind.Claude, settings.ChoiceFor(LlmUseRole.Planner).Provider);
        Assert.Equal(LlmProviderKind.Local, settings.ChoiceFor(LlmUseRole.Matcher).Provider);

        // The local model never plans; a model of another provider is refused; garbage falls back to the default.
        Assert.Throws<ArgumentException>(() => settings.SetChoice(LlmUseRole.Planner, new LlmChoice(LlmProviderKind.Local, LlmSettings.LocalModel)));
        Assert.Throws<ArgumentException>(() => settings.SetChoice(LlmUseRole.Planner, new LlmChoice(LlmProviderKind.Claude, "gpt-5")));
        Assert.Throws<ArgumentException>(() => settings.SetKey(LlmProviderKind.Local, "whatever-key"));
        store.Set("db1", "llm.role.planner", Encoding.UTF8.GetBytes("claude:claude-evil"));
        Assert.Equal(new LlmChoice(LlmProviderKind.DeepSeek, LlmSettings.StrongModel), settings.ChoiceFor(LlmUseRole.Planner));

        Assert.Equal(LlmSettings.DefaultLocalPort, settings.LocalPort);
        settings.LocalPort = 9000;
        Assert.Equal(9000, settings.LocalPort);
        Assert.Throws<ArgumentException>(() => settings.LocalPort = 22);
    }

    [Fact]
    public void A_deepseek_planner_choice_keeps_the_legacy_model_entry_in_step()
    {
        var settings = new LlmSettings(new InMemorySecretStore(), "db1");
        settings.SetChoice(LlmUseRole.Planner, new LlmChoice(LlmProviderKind.DeepSeek, LlmSettings.FastModel));
        Assert.Equal(LlmSettings.FastModel, settings.Model);
    }
}
