using System;
using System.Buffers;
using System.Collections.Generic;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace erp_inteligente.Core.Llm;

/// <summary>
/// Client of the "chat completions" wire format (non-streaming), shared by DeepSeek, OpenAI and a local
/// OpenAI-compatible server. The endpoint is fixed by each subclass, so a key can never be sent elsewhere; errors carry
/// only a kind and a status code, never the key or the response body.
/// </summary>
public abstract class ChatCompletionsProvider : ILlmProvider
{
    private const int MaxRetries = 2;

    private readonly HttpClient _http;
    private readonly Func<byte[]?>? _keySource;
    private readonly Func<TimeSpan, CancellationToken, Task> _delay;
    private readonly TimeSpan _timeout;

    /// <param name="keySource">Returns a fresh key buffer per call (zeroed after use); null for a server that needs no key.</param>
    protected ChatCompletionsProvider(HttpMessageHandler handler, Func<byte[]?>? keySource,
        Func<TimeSpan, CancellationToken, Task>? delay, TimeSpan? timeout)
    {
        ArgumentNullException.ThrowIfNull(handler);
        _keySource = keySource;
        _http = new HttpClient(handler, disposeHandler: false) { Timeout = System.Threading.Timeout.InfiniteTimeSpan };
        _delay = delay ?? Task.Delay;
        _timeout = timeout ?? TimeSpan.FromSeconds(60);
    }

    /// <summary>The fixed URL of the chat completions endpoint.</summary>
    protected abstract Uri Endpoint { get; }

    /// <summary>Name of the output limit in the body: <c>max_tokens</c> (DeepSeek, local) or <c>max_completion_tokens</c> (OpenAI).</summary>
    protected virtual string MaxTokensField => "max_tokens";

    /// <summary>DeepSeek returns <c>reasoning_content</c> and requires it back when tools are in use.</summary>
    protected virtual bool SendsReasoningBack => false;

    /// <summary>Handler without redirects, so the Authorization header cannot follow a redirect elsewhere.</summary>
    public static HttpMessageHandler CreateDefaultHandler() => new SocketsHttpHandler { AllowAutoRedirect = false };

    public Task<LlmResponse> CompleteAsync(LlmRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        return SendAsync(BuildBody(request, extra: null), cancellationToken);
    }

    /// <summary>Sends a body built by <see cref="BuildBody"/>; used by the connection tests of the subclasses.</summary>
    protected async Task<LlmResponse> SendAsync(byte[] body, CancellationToken ct)
    {
        for (var attempt = 0; ; attempt++)
        {
            ct.ThrowIfCancellationRequested();
            using var message = new HttpRequestMessage(HttpMethod.Post, Endpoint);
            if (_keySource is not null)
            {
                var key = _keySource();
                if (key is null || key.Length == 0)
                    throw new LlmException(LlmErrorKind.NotConfigured, "The LLM API key is not configured.");
                try { message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", Encoding.UTF8.GetString(key)); }
                finally { Array.Clear(key); }
            }
            message.Content = new ByteArrayContent(body);
            message.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");

            int status;
            string? text = null;
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(_timeout);
            try
            {
                using var response = await _http.SendAsync(message, timeoutCts.Token).ConfigureAwait(false);
                status = (int)response.StatusCode;
                if (response.IsSuccessStatusCode)
                    text = await response.Content.ReadAsStringAsync(timeoutCts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                throw new LlmException(LlmErrorKind.Timeout, "The LLM request timed out.");
            }
            catch (HttpRequestException)
            {
                throw new LlmException(LlmErrorKind.Unavailable, "The LLM service could not be reached.");
            }

            if (text is not null) return ParseResponse(text);

            if (status is 401 or 403)
                throw new LlmException(LlmErrorKind.Unauthorized, "The LLM provider rejected the credentials.", status);
            if (status == 429 || status >= 500)
            {
                if (attempt < MaxRetries)
                {
                    await _delay(TimeSpan.FromSeconds(attempt == 0 ? 1 : 3), ct).ConfigureAwait(false);
                    continue;
                }
                throw status == 429
                    ? new LlmException(LlmErrorKind.RateLimited, "The LLM provider is rate limiting requests.", status)
                    : new LlmException(LlmErrorKind.Unavailable, "The LLM service is unavailable.", status);
            }
            throw new LlmException(LlmErrorKind.InvalidResponse, "The LLM provider rejected the request.", status);
        }
    }

    /// <param name="extra">Writes provider-specific top-level fields (e.g. DeepSeek's <c>thinking</c>).</param>
    protected byte[] BuildBody(LlmRequest r, Action<Utf8JsonWriter>? extra)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using (var w = new Utf8JsonWriter(buffer))
        {
            w.WriteStartObject();
            w.WriteString("model", r.Model);
            w.WriteStartArray("messages");
            foreach (var m in r.Messages) WriteMessage(w, m);
            w.WriteEndArray();
            if (r.Tools.Count > 0)
            {
                w.WriteStartArray("tools");
                foreach (var t in r.Tools)
                {
                    w.WriteStartObject();
                    w.WriteString("type", "function");
                    w.WriteStartObject("function");
                    w.WriteString("name", t.Name);
                    w.WriteString("description", t.Description);
                    w.WritePropertyName("parameters");
                    using var schema = JsonDocument.Parse(t.ParametersSchema);
                    schema.RootElement.WriteTo(w);
                    w.WriteEndObject();
                    w.WriteEndObject();
                }
                w.WriteEndArray();
            }
            w.WriteNumber(MaxTokensField, r.MaxTokens);
            if (r.Temperature is { } temp) w.WriteNumber("temperature", temp);
            extra?.Invoke(w);
            w.WriteBoolean("stream", false);
            w.WriteEndObject();
        }
        return buffer.WrittenSpan.ToArray();
    }

    private void WriteMessage(Utf8JsonWriter w, LlmMessage m)
    {
        w.WriteStartObject();
        w.WriteString("role", m.Role switch
        {
            LlmRole.System => "system",
            LlmRole.User => "user",
            LlmRole.Assistant => "assistant",
            _ => "tool",
        });
        if (m.Content is not null) w.WriteString("content", m.Content);
        else if (m.Role != LlmRole.Assistant || m.ToolCalls.Count == 0) w.WriteString("content", string.Empty);
        else w.WriteNull("content");
        if (m.Role == LlmRole.Tool) w.WriteString("tool_call_id", m.ToolCallId);
        if (m.Role == LlmRole.Assistant)
        {
            if (SendsReasoningBack && m.ReasoningContent is not null) w.WriteString("reasoning_content", m.ReasoningContent);
            if (m.ToolCalls.Count > 0)
            {
                w.WriteStartArray("tool_calls");
                foreach (var c in m.ToolCalls)
                {
                    w.WriteStartObject();
                    w.WriteString("id", c.Id);
                    w.WriteString("type", "function");
                    w.WriteStartObject("function");
                    w.WriteString("name", c.Name);
                    w.WriteString("arguments", c.ArgumentsJson);
                    w.WriteEndObject();
                    w.WriteEndObject();
                }
                w.WriteEndArray();
            }
        }
        w.WriteEndObject();
    }

    private static LlmException Invalid() => new(LlmErrorKind.InvalidResponse, "The LLM response was not in the expected format.");

    private static LlmResponse ParseResponse(string text)
    {
        try
        {
            using var doc = JsonDocument.Parse(text);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || !root.TryGetProperty("choices", out var choices)
                || choices.ValueKind != JsonValueKind.Array || choices.GetArrayLength() == 0)
                throw Invalid();
            var choice = choices[0];
            if (!choice.TryGetProperty("message", out var msg) || msg.ValueKind != JsonValueKind.Object) throw Invalid();

            var calls = new List<LlmToolCall>();
            if (msg.TryGetProperty("tool_calls", out var tc) && tc.ValueKind == JsonValueKind.Array)
            {
                foreach (var c in tc.EnumerateArray())
                {
                    if (!c.TryGetProperty("function", out var fn)) throw Invalid();
                    var id = Str(c, "id");
                    var name = Str(fn, "name");
                    var args = Str(fn, "arguments");
                    if (id is null || name is null) throw Invalid();
                    calls.Add(new LlmToolCall(id, name, args ?? "{}"));
                }
            }

            var finish = Str(choice, "finish_reason") switch
            {
                "stop" => LlmFinishReason.Stop,
                "tool_calls" => LlmFinishReason.ToolCalls,
                "length" => LlmFinishReason.Length,
                "content_filter" => LlmFinishReason.ContentFilter,
                _ => LlmFinishReason.Other,
            };

            var usage = new LlmUsage(0, 0);
            if (root.TryGetProperty("usage", out var u) && u.ValueKind == JsonValueKind.Object)
                usage = new LlmUsage(Int(u, "prompt_tokens"), Int(u, "completion_tokens"));

            return new LlmResponse(Str(msg, "content"), calls, Str(msg, "reasoning_content"), finish, usage);
        }
        catch (Exception e) when (e is JsonException or InvalidOperationException or FormatException)
        {
            throw Invalid();
        }
    }

    private static string? Str(JsonElement e, string name)
        => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static int Int(JsonElement e, string name)
        => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var n) ? n : 0;
}
