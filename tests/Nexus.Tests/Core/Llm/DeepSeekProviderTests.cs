using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using erp_inteligente.Core.Llm;
using Xunit;

namespace Nexus.Tests.Core.Llm;

public class DeepSeekProviderTests
{
    private const string Key = "sk-test-SECRETKEY-1234567890";

    private sealed class FakeHandler(Func<HttpRequestMessage, int, Task<HttpResponseMessage>> respond) : HttpMessageHandler
    {
        public List<(HttpRequestMessage Request, string Body)> Calls { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(ct);
            Calls.Add((request, body));
            return await respond(request, Calls.Count);
        }
    }

    private static HttpResponseMessage Json(HttpStatusCode code, string body) =>
        new(code) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    private static string Ok(string message, string finish = "stop", string usage = "{\"prompt_tokens\":11,\"completion_tokens\":7}") =>
        $"{{\"choices\":[{{\"index\":0,\"message\":{message},\"finish_reason\":\"{finish}\"}}],\"usage\":{usage}}}";

    private static (DeepSeekProvider, FakeHandler) Make(Func<HttpRequestMessage, int, Task<HttpResponseMessage>> respond,
        Func<byte[]?>? key = null, TimeSpan? timeout = null, List<TimeSpan>? delays = null)
    {
        var handler = new FakeHandler(respond);
        var provider = new DeepSeekProvider(handler, key ?? (() => Encoding.UTF8.GetBytes(Key)),
            (d, _) => { delays?.Add(d); return Task.CompletedTask; }, timeout);
        return (provider, handler);
    }

    private static LlmRequest Req(IReadOnlyList<LlmTool>? tools = null, params LlmMessage[] messages) =>
        new("deepseek-v4-pro", messages.Length > 0 ? messages : [LlmMessage.User("oi")], tools ?? [], 1000);

    private static void AssertNoKey(Exception e)
    {
        Assert.DoesNotContain(Key, e.Message);
        Assert.DoesNotContain(Key, e.ToString());
    }

    [Fact]
    public async Task Request_IsSerializedAsExpected()
    {
        var (p, h) = Make((_, _) => Task.FromResult(Json(HttpStatusCode.OK, Ok("{\"role\":\"assistant\",\"content\":\"ok\"}"))));
        var calls = new[] { new LlmToolCall("c1", "get_stock", "{\"sku\":\"A\"}") };
        var tool = new LlmTool("get_stock", "Reads stock", "{\"type\":\"object\",\"properties\":{\"sku\":{\"type\":\"string\"}}}");
        await p.CompleteAsync(new LlmRequest("deepseek-v4-pro",
            [LlmMessage.System("sys"), LlmMessage.User("u"), LlmMessage.Assistant(null, calls, "thinking"), LlmMessage.ToolResult("c1", "{\"qty\":3}")],
            [tool], 500, 0.2), CancellationToken.None);

        var (req, body) = Assert.Single(h.Calls);
        Assert.Equal("https://api.deepseek.com/chat/completions", req.RequestUri!.ToString());
        Assert.Equal(HttpMethod.Post, req.Method);
        Assert.Equal("Bearer", req.Headers.Authorization!.Scheme);
        Assert.Equal(Key, req.Headers.Authorization.Parameter);

        using var doc = JsonDocument.Parse(body);
        var root = doc.RootElement;
        Assert.Equal("deepseek-v4-pro", root.GetProperty("model").GetString());
        Assert.Equal(500, root.GetProperty("max_tokens").GetInt32());
        Assert.False(root.GetProperty("stream").GetBoolean());
        Assert.Equal(0.2, root.GetProperty("temperature").GetDouble());
        var msgs = root.GetProperty("messages");
        Assert.Equal(4, msgs.GetArrayLength());
        Assert.Equal("system", msgs[0].GetProperty("role").GetString());
        var asst = msgs[2];
        Assert.Equal("thinking", asst.GetProperty("reasoning_content").GetString());
        Assert.Equal(JsonValueKind.Null, asst.GetProperty("content").ValueKind);
        var tc = asst.GetProperty("tool_calls")[0];
        Assert.Equal("c1", tc.GetProperty("id").GetString());
        Assert.Equal("function", tc.GetProperty("type").GetString());
        Assert.Equal("get_stock", tc.GetProperty("function").GetProperty("name").GetString());
        Assert.Equal("{\"sku\":\"A\"}", tc.GetProperty("function").GetProperty("arguments").GetString());
        Assert.Equal("tool", msgs[3].GetProperty("role").GetString());
        Assert.Equal("c1", msgs[3].GetProperty("tool_call_id").GetString());
        var t = root.GetProperty("tools")[0];
        Assert.Equal("function", t.GetProperty("type").GetString());
        Assert.Equal("object", t.GetProperty("function").GetProperty("parameters").GetProperty("type").GetString());
        Assert.DoesNotContain(Key, body);
    }

    [Fact]
    public async Task Request_OmitsToolsAndTemperatureWhenAbsent()
    {
        var (p, h) = Make((_, _) => Task.FromResult(Json(HttpStatusCode.OK, Ok("{\"role\":\"assistant\",\"content\":\"ok\"}"))));
        await p.CompleteAsync(Req(), CancellationToken.None);
        using var doc = JsonDocument.Parse(h.Calls[0].Body);
        Assert.False(doc.RootElement.TryGetProperty("tools", out _));
        Assert.False(doc.RootElement.TryGetProperty("temperature", out _));
    }

    [Fact]
    public async Task Response_Text_Usage_Reasoning()
    {
        var (p, _) = Make((_, _) => Task.FromResult(Json(HttpStatusCode.OK,
            Ok("{\"role\":\"assistant\",\"content\":\"olá\",\"reasoning_content\":\"hmm\"}"))));
        var r = await p.CompleteAsync(Req(), CancellationToken.None);
        Assert.Equal("olá", r.Content);
        Assert.Equal("hmm", r.ReasoningContent);
        Assert.Empty(r.ToolCalls);
        Assert.Equal(LlmFinishReason.Stop, r.FinishReason);
        Assert.Equal(new LlmUsage(11, 7), r.Usage);
    }

    [Fact]
    public async Task Response_ToolCalls()
    {
        var msg = "{\"role\":\"assistant\",\"content\":null,\"tool_calls\":[{\"id\":\"x1\",\"type\":\"function\",\"function\":{\"name\":\"a\",\"arguments\":\"{\\\"k\\\":1}\"}},{\"id\":\"x2\",\"type\":\"function\",\"function\":{\"name\":\"b\",\"arguments\":\"not json\"}}]}";
        var (p, _) = Make((_, _) => Task.FromResult(Json(HttpStatusCode.OK, Ok(msg, "tool_calls"))));
        var r = await p.CompleteAsync(Req(), CancellationToken.None);
        Assert.Null(r.Content);
        Assert.Equal(LlmFinishReason.ToolCalls, r.FinishReason);
        Assert.Equal(2, r.ToolCalls.Count);
        Assert.Equal(new LlmToolCall("x1", "a", "{\"k\":1}"), r.ToolCalls[0]);
        Assert.Equal("not json", r.ToolCalls[1].ArgumentsJson);
    }

    [Theory]
    [InlineData("stop", LlmFinishReason.Stop)]
    [InlineData("tool_calls", LlmFinishReason.ToolCalls)]
    [InlineData("length", LlmFinishReason.Length)]
    [InlineData("content_filter", LlmFinishReason.ContentFilter)]
    [InlineData("insufficient_system_resource", LlmFinishReason.Other)]
    [InlineData("aborted", LlmFinishReason.Other)]
    public async Task Response_FinishReasons(string raw, LlmFinishReason expected)
    {
        var (p, _) = Make((_, _) => Task.FromResult(Json(HttpStatusCode.OK, Ok("{\"role\":\"assistant\",\"content\":\"x\"}", raw))));
        Assert.Equal(expected, (await p.CompleteAsync(Req(), CancellationToken.None)).FinishReason);
    }

    [Fact]
    public async Task Unauthorized_NoRetry()
    {
        var (p, h) = Make((_, _) => Task.FromResult(Json(HttpStatusCode.Unauthorized, $"{{\"error\":\"bad {Key}\"}}")));
        var e = await Assert.ThrowsAsync<LlmException>(() => p.CompleteAsync(Req(), CancellationToken.None));
        Assert.Equal(LlmErrorKind.Unauthorized, e.Kind);
        Assert.Single(h.Calls);
        AssertNoKey(e);
    }

    [Fact]
    public async Task RateLimited_ThenOk_Retries()
    {
        var delays = new List<TimeSpan>();
        var (p, h) = Make((_, n) => Task.FromResult(n == 1
            ? Json(HttpStatusCode.TooManyRequests, "{}")
            : Json(HttpStatusCode.OK, Ok("{\"role\":\"assistant\",\"content\":\"ok\"}"))), delays: delays);
        var r = await p.CompleteAsync(Req(), CancellationToken.None);
        Assert.Equal("ok", r.Content);
        Assert.Equal(2, h.Calls.Count);
        Assert.Single(delays);
    }

    [Fact]
    public async Task ServerError_ThreeAttempts_ThenUnavailable()
    {
        var delays = new List<TimeSpan>();
        var (p, h) = Make((_, _) => Task.FromResult(Json(HttpStatusCode.InternalServerError, $"boom {Key}")), delays: delays);
        var e = await Assert.ThrowsAsync<LlmException>(() => p.CompleteAsync(Req(), CancellationToken.None));
        Assert.Equal(LlmErrorKind.Unavailable, e.Kind);
        Assert.Equal(3, h.Calls.Count);
        Assert.Equal(2, delays.Count);
        Assert.True(delays[1] > delays[0]);
        AssertNoKey(e);
    }

    [Fact]
    public async Task RateLimited_Persistent_ThrowsRateLimited()
    {
        var (p, h) = Make((_, _) => Task.FromResult(Json(HttpStatusCode.TooManyRequests, "{}")));
        var e = await Assert.ThrowsAsync<LlmException>(() => p.CompleteAsync(Req(), CancellationToken.None));
        Assert.Equal(LlmErrorKind.RateLimited, e.Kind);
        Assert.Equal(3, h.Calls.Count);
        AssertNoKey(e);
    }

    [Fact]
    public async Task OtherClientError_IsInvalidResponse_NoRetry()
    {
        var (p, h) = Make((_, _) => Task.FromResult(Json(HttpStatusCode.BadRequest, $"{{\"error\":\"{Key}\"}}")));
        var e = await Assert.ThrowsAsync<LlmException>(() => p.CompleteAsync(Req(), CancellationToken.None));
        Assert.Equal(LlmErrorKind.InvalidResponse, e.Kind);
        Assert.Single(h.Calls);
        AssertNoKey(e);
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("{}")]
    [InlineData("{\"choices\":[]}")]
    [InlineData("{\"choices\":[{\"finish_reason\":\"stop\"}]}")]
    [InlineData("[1,2]")]
    public async Task MalformedBody_IsInvalidResponse(string body)
    {
        var (p, _) = Make((_, _) => Task.FromResult(Json(HttpStatusCode.OK, body)));
        var e = await Assert.ThrowsAsync<LlmException>(() => p.CompleteAsync(Req(), CancellationToken.None));
        Assert.Equal(LlmErrorKind.InvalidResponse, e.Kind);
        AssertNoKey(e);
    }

    [Fact]
    public async Task Timeout_IsLlmTimeout()
    {
        var handler = new HonoringHandler();
        var p2 = new DeepSeekProvider(handler, () => Encoding.UTF8.GetBytes(Key), (_, _) => Task.CompletedTask, TimeSpan.FromMilliseconds(50));
        var e = await Assert.ThrowsAsync<LlmException>(() => p2.CompleteAsync(Req(), CancellationToken.None));
        Assert.Equal(LlmErrorKind.Timeout, e.Kind);
        AssertNoKey(e);
    }

    private sealed class HonoringHandler : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            await Task.Delay(Timeout.Infinite, ct);
            return new HttpResponseMessage();
        }
    }

    [Fact]
    public async Task CallerCancellation_RethrowsOperationCanceled()
    {
        using var cts = new CancellationTokenSource();
        var p = new DeepSeekProvider(new HonoringHandler(), () => Encoding.UTF8.GetBytes(Key), (_, _) => Task.CompletedTask);
        var task = p.CompleteAsync(Req(), cts.Token);
        cts.CancelAfter(50);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => task);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MissingKey_NotConfigured_NoHttpCall(bool empty)
    {
        var (p, h) = Make((_, _) => Task.FromResult(Json(HttpStatusCode.OK, "{}")), key: () => empty ? [] : null);
        var e = await Assert.ThrowsAsync<LlmException>(() => p.CompleteAsync(Req(), CancellationToken.None));
        Assert.Equal(LlmErrorKind.NotConfigured, e.Kind);
        Assert.Empty(h.Calls);
    }

    [Fact]
    public async Task KeyBuffer_IsZeroedAfterUse()
    {
        var buf = Encoding.UTF8.GetBytes(Key);
        var (p, _) = Make((_, _) => Task.FromResult(Json(HttpStatusCode.OK, Ok("{\"role\":\"assistant\",\"content\":\"ok\"}"))), key: () => buf);
        await p.CompleteAsync(Req(), CancellationToken.None);
        Assert.All(buf, b => Assert.Equal(0, b));
    }

    [Fact]
    public async Task TestConnection_SendsMinimalRequest()
    {
        var (p, h) = Make((_, _) => Task.FromResult(Json(HttpStatusCode.OK, Ok("{\"role\":\"assistant\",\"content\":\"p\"}"))));
        await p.TestConnectionAsync(CancellationToken.None);
        using var doc = JsonDocument.Parse(h.Calls[0].Body);
        Assert.Equal(1, doc.RootElement.GetProperty("max_tokens").GetInt32());
        Assert.Equal("disabled", doc.RootElement.GetProperty("thinking").GetProperty("type").GetString());
    }

    [Fact]
    public async Task TestConnection_Unauthorized_Throws()
    {
        var (p, _) = Make((_, _) => Task.FromResult(Json(HttpStatusCode.Unauthorized, "{}")));
        var e = await Assert.ThrowsAsync<LlmException>(() => p.TestConnectionAsync(CancellationToken.None));
        Assert.Equal(LlmErrorKind.Unauthorized, e.Kind);
    }
}
