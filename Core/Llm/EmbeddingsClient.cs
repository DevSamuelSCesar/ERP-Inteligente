using System;
using System.Buffers;
using System.Collections.Generic;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using erp_inteligente.Core.Knowledge;

namespace erp_inteligente.Core.Llm;

/// <summary>
/// Client of the OpenAI "embeddings" wire format, used by OpenAI and by a local OpenAI-compatible server. Same rules as
/// <see cref="ChatCompletionsProvider"/>: fixed endpoint, no redirects (<see cref="ChatCompletionsProvider.CreateDefaultHandler"/>),
/// the key read per call and zeroed, errors with a kind and a status code only, never the key, the text or the body.
/// </summary>
public sealed class EmbeddingsClient
{
    public const int MaxBatch = 64;
    public const int MaxTextLength = 8000;
    private const int MaxRetries = 2;

    private readonly HttpClient _http;
    private readonly Func<Uri> _endpoint;
    private readonly Func<byte[]?>? _keySource;
    private readonly Func<TimeSpan, CancellationToken, Task> _delay;
    private readonly TimeSpan _timeout;

    /// <param name="endpoint">Read per call (the local port may change); must be api.openai.com or loopback.</param>
    /// <param name="keySource">A fresh key buffer per call, zeroed after use; null for a local server.</param>
    public EmbeddingsClient(HttpMessageHandler handler, Func<Uri> endpoint, Func<byte[]?>? keySource,
        Func<TimeSpan, CancellationToken, Task>? delay = null, TimeSpan? timeout = null)
    {
        ArgumentNullException.ThrowIfNull(handler);
        _endpoint = endpoint ?? throw new ArgumentNullException(nameof(endpoint));
        _keySource = keySource;
        _http = new HttpClient(handler, disposeHandler: false) { Timeout = Timeout.InfiniteTimeSpan };
        _delay = delay ?? Task.Delay;
        _timeout = timeout ?? TimeSpan.FromSeconds(30);
    }

    public static readonly Uri OpenAiEndpoint = new("https://api.openai.com/v1/embeddings");

    public static Uri LocalEndpoint(int port) => port is >= 1024 and <= 65535
        ? new Uri($"http://127.0.0.1:{port}/v1/embeddings")
        : throw new LlmException(LlmErrorKind.NotConfigured, "The local model port is not configured.");

    /// <summary>One vector per text, in order. Texts longer than <see cref="MaxTextLength"/> are cut.</summary>
    public async Task<EmbeddingBatch> EmbedAsync(string model, IReadOnlyList<string> texts, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(model);
        ArgumentNullException.ThrowIfNull(texts);
        if (texts.Count == 0) return new EmbeddingBatch([], 0);
        if (texts.Count > MaxBatch) throw new ArgumentException($"At most {MaxBatch} texts per call.", nameof(texts));

        var endpoint = _endpoint();
        if (!(endpoint == OpenAiEndpoint || (endpoint.IsLoopback && endpoint.Scheme == Uri.UriSchemeHttp && endpoint.Host == "127.0.0.1")))
            throw new LlmException(LlmErrorKind.NotConfigured, "Embeddings go only to OpenAI or to 127.0.0.1.");
        var body = BuildBody(model, texts);

        for (var attempt = 0; ; attempt++)
        {
            ct.ThrowIfCancellationRequested();
            using var message = new HttpRequestMessage(HttpMethod.Post, endpoint);
            if (_keySource is not null)
            {
                var key = _keySource();
                if (key is null || key.Length == 0)
                    throw new LlmException(LlmErrorKind.NotConfigured, "The embeddings API key is not configured.");
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
                throw new LlmException(LlmErrorKind.Timeout, "The embeddings request timed out.");
            }
            catch (HttpRequestException)
            {
                throw new LlmException(LlmErrorKind.Unavailable, "The embeddings service could not be reached.");
            }

            if (text is not null) return Parse(text, texts.Count);
            if (status is 401 or 403)
                throw new LlmException(LlmErrorKind.Unauthorized, "The embeddings provider rejected the credentials.", status);
            if (status == 429 || status >= 500)
            {
                if (attempt < MaxRetries)
                {
                    await _delay(TimeSpan.FromSeconds(attempt == 0 ? 1 : 3), ct).ConfigureAwait(false);
                    continue;
                }
                throw status == 429
                    ? new LlmException(LlmErrorKind.RateLimited, "The embeddings provider is rate limiting requests.", status)
                    : new LlmException(LlmErrorKind.Unavailable, "The embeddings service is unavailable.", status);
            }
            throw new LlmException(LlmErrorKind.InvalidResponse, "The embeddings provider rejected the request.", status);
        }
    }

    private static byte[] BuildBody(string model, IReadOnlyList<string> texts)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using (var w = new Utf8JsonWriter(buffer))
        {
            w.WriteStartObject();
            w.WriteString("model", model);
            w.WriteStartArray("input");
            foreach (var t in texts)
                w.WriteStringValue(string.IsNullOrWhiteSpace(t) ? " " : t.Length > MaxTextLength ? t[..MaxTextLength] : t);
            w.WriteEndArray();
            w.WriteString("encoding_format", "float");
            w.WriteEndObject();
        }
        return buffer.WrittenSpan.ToArray();
    }

    /// <summary>Reads <c>data[].embedding</c> by <c>index</c>; every vector must have the same, non-zero length.</summary>
    internal static EmbeddingBatch Parse(string json, int expected)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            if (!root.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array || data.GetArrayLength() != expected)
                throw Invalid();
            var vectors = new float[expected][];
            var dimensions = -1;
            var position = 0;
            foreach (var item in data.EnumerateArray())
            {
                var index = item.TryGetProperty("index", out var i) && i.TryGetInt32(out var n) ? n : position;
                position++;
                if (index < 0 || index >= expected || vectors[index] is not null) throw Invalid();
                if (!item.TryGetProperty("embedding", out var embedding) || embedding.ValueKind != JsonValueKind.Array) throw Invalid();
                var vector = new float[embedding.GetArrayLength()];
                var k = 0;
                foreach (var value in embedding.EnumerateArray())
                {
                    if (value.ValueKind != JsonValueKind.Number) throw Invalid();
                    var f = value.GetSingle();
                    if (!float.IsFinite(f)) throw Invalid();
                    vector[k++] = f;
                }
                if (vector.Length == 0 || vector.Length > 4096 || (dimensions >= 0 && vector.Length != dimensions)) throw Invalid();
                dimensions = vector.Length;
                vectors[index] = vector;
            }
            var tokens = root.TryGetProperty("usage", out var usage) && usage.TryGetProperty("prompt_tokens", out var p)
                         && p.TryGetInt32(out var count) ? Math.Max(0, count) : 0;
            return new EmbeddingBatch(vectors, tokens);
        }
        catch (JsonException)
        {
            throw Invalid();
        }
        catch (FormatException)
        {
            throw Invalid();
        }
    }

    private static LlmException Invalid() => new(LlmErrorKind.InvalidResponse, "The embeddings response was not understood.");
}

/// <summary>
/// The embeddings provider picked in Configurações (<see cref="LlmSettings.EmbeddingChoice"/>), read on every call: OpenAI
/// <c>text-embedding-3-small</c> with the OpenAI key, or a model served on 127.0.0.1 (the local port of the Matcher). Off
/// when nothing was chosen.
/// </summary>
public sealed class ConfiguredEmbeddingProvider : IEmbeddingProvider
{
    private readonly LlmSettings _settings;
    private readonly EmbeddingsClient _openAi;
    private readonly EmbeddingsClient _local;

    public ConfiguredEmbeddingProvider(LlmSettings settings, HttpMessageHandler handler,
        Func<TimeSpan, CancellationToken, Task>? delay = null, TimeSpan? timeout = null)
    {
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _openAi = new EmbeddingsClient(handler, () => EmbeddingsClient.OpenAiEndpoint, () => settings.ReadKey(LlmProviderKind.OpenAI), delay, timeout);
        _local = new EmbeddingsClient(handler, () => EmbeddingsClient.LocalEndpoint(settings.LocalPort), null, delay, timeout);
    }

    public string? Model => _settings.EmbeddingChoice is { } c ? LlmSettings.EmbeddingModelId(c) : null;

    public EmbeddingThresholds Thresholds => _settings.EmbeddingChoice is { } c
        ? LlmSettings.EmbeddingThresholdsFor(c)
        : NullEmbeddingProvider.Instance.Thresholds;

    public Task<EmbeddingBatch> EmbedAsync(IReadOnlyList<string> texts, CancellationToken cancellationToken)
    {
        var choice = _settings.EmbeddingChoice
            ?? throw new LlmException(LlmErrorKind.NotConfigured, "Embeddings are off.");
        return (choice.Provider == LlmProviderKind.Local ? _local : _openAi).EmbedAsync(choice.Model, texts, cancellationToken);
    }
}
