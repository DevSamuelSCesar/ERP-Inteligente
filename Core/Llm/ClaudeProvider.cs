using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Anthropic;
using Anthropic.Exceptions;
using Anthropic.Models.Messages;

namespace erp_inteligente.Core.Llm;

/// <summary>
/// Claude (Anthropic Messages API) through the official <c>Anthropic</c> SDK, non-streaming, feature 8.
/// <list type="bullet">
/// <item>The key is read from the OS key store on every call and lives only in the client of that call.</item>
/// <item>The transport is injected (no redirects by default; a fake handler in tests); the host is the SDK's default.</item>
/// <item>An assistant turn keeps its original content blocks (<see cref="LlmMessage.ProviderState"/>), so signed thinking
/// blocks go back unchanged in the tool loop, as the API requires.</item>
/// <item>Consecutive tool results become one user message with several <c>tool_result</c> blocks.</item>
/// <item>No sampling parameters and no thinking configuration: current models reject them; adaptive thinking is the default.</item>
/// </list>
/// </summary>
public sealed class ClaudeProvider : ILlmProvider
{
    private readonly HttpMessageHandler _handler;
    private readonly Func<byte[]?> _keySource;
    private readonly TimeSpan _timeout;
    private readonly int _maxRetries;

    /// <param name="maxRetries">Retries of the SDK for 429, 5xx (529 included) and network errors; 2 by default.</param>
    public ClaudeProvider(HttpMessageHandler handler, Func<byte[]?> keySource, TimeSpan? timeout = null, int maxRetries = 2)
    {
        _handler = handler ?? throw new ArgumentNullException(nameof(handler));
        _keySource = keySource ?? throw new ArgumentNullException(nameof(keySource));
        _timeout = timeout ?? TimeSpan.FromSeconds(120);
        _maxRetries = Math.Clamp(maxRetries, 0, 5);
    }

    public async Task<LlmResponse> CompleteAsync(LlmRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var parameters = BuildParams(request);
        var key = _keySource();
        if (key is null || key.Length == 0)
            throw new LlmException(LlmErrorKind.NotConfigured, "The LLM API key is not configured.");

        string apiKey;
        try { apiKey = Encoding.UTF8.GetString(key); }
        finally { Array.Clear(key); }

        using var http = new HttpClient(_handler, disposeHandler: false) { Timeout = System.Threading.Timeout.InfiniteTimeSpan };
        var client = new AnthropicClient { ApiKey = apiKey, HttpClient = http, MaxRetries = _maxRetries, Timeout = _timeout };
        try
        {
            var message = await client.Messages.Create(parameters, cancellationToken).ConfigureAwait(false);
            return Map(message);
        }
        catch (AnthropicUnauthorizedException) { throw new LlmException(LlmErrorKind.Unauthorized, "The LLM provider rejected the credentials.", 401); }
        catch (AnthropicForbiddenException) { throw new LlmException(LlmErrorKind.Unauthorized, "The LLM provider rejected the credentials.", 403); }
        catch (AnthropicRateLimitException) { throw new LlmException(LlmErrorKind.RateLimited, "The LLM provider is rate limiting requests.", 429); }
        catch (Anthropic5xxException) { throw new LlmException(LlmErrorKind.Unavailable, "The LLM service is unavailable."); }
        catch (AnthropicIOException) { throw new LlmException(LlmErrorKind.Unavailable, "The LLM service could not be reached."); }
        catch (AnthropicApiException) { throw new LlmException(LlmErrorKind.InvalidResponse, "The LLM provider rejected the request."); }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new LlmException(LlmErrorKind.Timeout, "The LLM request timed out.");
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or FormatException)
        {
            throw new LlmException(LlmErrorKind.InvalidResponse, "The LLM response was not in the expected format.");
        }
    }

    /// <summary>Minimal call to the cheapest offered model; throws <see cref="LlmException"/> when the key or service is unusable.</summary>
    public Task TestConnectionAsync(CancellationToken cancellationToken) =>
        CompleteAsync(new LlmRequest(LlmSettings.ClaudeFastModel, [LlmMessage.User("ping")], [], 64), cancellationToken);

    // ---- request ---------------------------------------------------------------------------------------------

    internal static MessageCreateParams BuildParams(LlmRequest request)
    {
        var system = string.Join("\n\n", request.Messages.Where(m => m.Role == LlmRole.System).Select(m => m.Content ?? ""));
        var messages = new List<MessageParam>();
        List<ContentBlockParam>? pendingResults = null;

        void FlushResults()
        {
            if (pendingResults is null) return;
            messages.Add(new UserMessageParam { Content = pendingResults });
            pendingResults = null;
        }

        foreach (var m in request.Messages)
        {
            switch (m.Role)
            {
                case LlmRole.System:
                    continue;
                case LlmRole.Tool:
                    pendingResults ??= [];
                    pendingResults.Add(new ToolResultBlockParam { ToolUseID = m.ToolCallId ?? "", Content = m.Content ?? "" });
                    continue;
                case LlmRole.User:
                    FlushResults();
                    messages.Add(new UserMessageParam { Content = m.Content ?? "" });
                    continue;
                default:
                    FlushResults();
                    messages.Add(new AssistantMessageParam { Content = AssistantBlocks(m) });
                    continue;
            }
        }
        FlushResults();

        // Prompt caching: tools, then system, then messages is the prefix order. A breakpoint on the system prompt caches
        // the tool definitions and the prompt, which repeat on every request; the top-level breakpoint caches the
        // conversation up to the last message, so each round of the tool loop rereads the earlier rounds from the cache.
        var parameters = new MessageCreateParams
        {
            Model = request.Model,
            MaxTokens = request.MaxTokens,
            Messages = messages,
            CacheControl = new CacheControlEphemeral(),
        };
        if (system.Length > 0)
            parameters = parameters with
            {
                System = new List<TextBlockParam> { new() { Text = system, CacheControl = new CacheControlEphemeral() } },
            };
        if (request.Tools.Count > 0)
        {
            var tools = request.Tools.Select(ToTool).ToList();
            if (system.Length == 0) tools[^1] = tools[^1] with { CacheControl = new CacheControlEphemeral() };
            parameters = parameters with { Tools = tools.Select(t => (ToolUnion)t).ToList() };
        }
        return parameters;
    }

    /// <summary>The original blocks when this provider produced the turn; otherwise text and tool calls rebuilt.</summary>
    private static List<ContentBlockParam> AssistantBlocks(LlmMessage m)
    {
        if (m.ProviderState is List<ContentBlockParam> original) return original;
        var blocks = new List<ContentBlockParam>();
        if (!string.IsNullOrEmpty(m.Content)) blocks.Add(new TextBlockParam { Text = m.Content });
        foreach (var call in m.ToolCalls)
            blocks.Add(new ToolUseBlockParam { ID = call.Id, Name = call.Name, Input = ParseInput(call.ArgumentsJson) });
        if (blocks.Count == 0) blocks.Add(new TextBlockParam { Text = "." });
        return blocks;
    }

    private static Dictionary<string, JsonElement> ParseInput(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(json) ? "{}" : json);
            if (doc.RootElement.ValueKind == JsonValueKind.Object)
                return doc.RootElement.EnumerateObject().ToDictionary(p => p.Name, p => p.Value.Clone(), StringComparer.Ordinal);
        }
        catch (JsonException)
        {
        }
        return [];
    }

    /// <summary>Our tools are JSON Schema objects: their properties and required list become the SDK's input schema.</summary>
    private static Tool ToTool(LlmTool tool)
    {
        using var doc = JsonDocument.Parse(tool.ParametersSchema);
        var root = doc.RootElement;
        var properties = root.TryGetProperty("properties", out var props) && props.ValueKind == JsonValueKind.Object
            ? props.EnumerateObject().ToDictionary(p => p.Name, p => p.Value.Clone(), StringComparer.Ordinal)
            : new Dictionary<string, JsonElement>();
        var required = root.TryGetProperty("required", out var req) && req.ValueKind == JsonValueKind.Array
            ? req.EnumerateArray().Where(e => e.ValueKind == JsonValueKind.String).Select(e => e.GetString()!).ToList()
            : [];
        return new Tool
        {
            Name = tool.Name,
            Description = tool.Description,
            InputSchema = new() { Properties = properties, Required = required },
        };
    }

    // ---- response --------------------------------------------------------------------------------------------

    internal static LlmResponse Map(Message message)
    {
        var text = new StringBuilder();
        var calls = new List<LlmToolCall>();
        var echo = new List<ContentBlockParam>();
        foreach (var block in message.Content)
        {
            if (block.TryPickText(out TextBlock? t))
            {
                if (text.Length > 0) text.Append('\n');
                text.Append(t.Text);
                echo.Add(new TextBlockParam { Text = t.Text });
            }
            else if (block.TryPickThinking(out ThinkingBlock? thinking))
                echo.Add(new ThinkingBlockParam { Thinking = thinking.Thinking, Signature = thinking.Signature });
            else if (block.TryPickRedactedThinking(out RedactedThinkingBlock? redacted))
                echo.Add(new RedactedThinkingBlockParam { Data = redacted.Data });
            else if (block.TryPickToolUse(out ToolUseBlock? use))
            {
                calls.Add(new LlmToolCall(use.ID, use.Name, JsonSerializer.Serialize(use.Input)));
                echo.Add(new ToolUseBlockParam { ID = use.ID, Name = use.Name, Input = use.Input });
            }
        }

        var finish = StopReasonText(message) switch
        {
            "end_turn" or "stop_sequence" => LlmFinishReason.Stop,
            "tool_use" => LlmFinishReason.ToolCalls,
            "max_tokens" => LlmFinishReason.Length,
            "refusal" => LlmFinishReason.ContentFilter,
            _ => LlmFinishReason.Other,
        };
        // InputTokens counts only the uncached part: the prompt total adds what was written to and read from the cache.
        var cacheWrite = message.Usage.CacheCreationInputTokens ?? 0;
        var cacheRead = message.Usage.CacheReadInputTokens ?? 0;
        var usage = new LlmUsage(Clamp(message.Usage.InputTokens + cacheWrite + cacheRead), Clamp(message.Usage.OutputTokens))
        {
            CacheReadTokens = Clamp(cacheRead),
            CacheWriteTokens = Clamp(cacheWrite),
        };
        return new LlmResponse(text.Length > 0 ? text.ToString() : null, calls, null, finish, usage) { ProviderState = echo };
    }

    private static int Clamp(long value) => (int)Math.Clamp(value, 0, int.MaxValue);

    /// <summary>The wire value ("end_turn", "tool_use"...), so a reason added after this SDK version still maps to Other.</summary>
    private static string? StopReasonText(Message message) => message.StopReason?.Raw();
}
