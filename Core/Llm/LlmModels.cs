using System.Threading;
using System.Threading.Tasks;
using System;
using System.Collections.Generic;

namespace erp_inteligente.Core.Llm;

/// <summary>Role of a chat message.</summary>
public enum LlmRole { System, User, Assistant, Tool }

/// <summary>Why the model stopped generating.</summary>
public enum LlmFinishReason { Stop, ToolCalls, Length, ContentFilter, Other }

/// <summary>A function call requested by the model. <see cref="ArgumentsJson"/> is raw and must be validated by the caller.</summary>
public sealed record LlmToolCall(string Id, string Name, string ArgumentsJson);

/// <summary>A function the model may call. <see cref="ParametersSchema"/> is JSON Schema text.</summary>
public sealed record LlmTool(string Name, string Description, string ParametersSchema);

/// <summary>One chat message; use the static factories.</summary>
public sealed record LlmMessage
{
    public required LlmRole Role { get; init; }
    public string? Content { get; init; }
    public IReadOnlyList<LlmToolCall> ToolCalls { get; init; } = [];
    /// <summary>Thinking text of an assistant turn; must be passed back verbatim when tools are in use.</summary>
    public string? ReasoningContent { get; init; }
    /// <summary>Id of the call a <see cref="LlmRole.Tool"/> message answers.</summary>
    public string? ToolCallId { get; init; }

    /// <summary>
    /// Provider-specific copy of an assistant turn that must be sent back unchanged (Claude's content blocks, with the
    /// signed thinking blocks). Opaque to everything but the provider that produced it; null for the others.
    /// </summary>
    public object? ProviderState { get; init; }

    public static LlmMessage System(string content) => new() { Role = LlmRole.System, Content = content };
    public static LlmMessage User(string content) => new() { Role = LlmRole.User, Content = content };

    public static LlmMessage Assistant(string? content, IReadOnlyList<LlmToolCall>? toolCalls = null, string? reasoningContent = null)
        => new() { Role = LlmRole.Assistant, Content = content, ToolCalls = toolCalls ?? [], ReasoningContent = reasoningContent };

    /// <summary>Rebuilds the assistant message to append to the history from a model response.</summary>
    public static LlmMessage Assistant(LlmResponse response)
    {
        ArgumentNullException.ThrowIfNull(response);
        return Assistant(response.Content, response.ToolCalls, response.ReasoningContent) with { ProviderState = response.ProviderState };
    }

    public static LlmMessage ToolResult(string toolCallId, string content)
        => new() { Role = LlmRole.Tool, ToolCallId = toolCallId, Content = content };
}

/// <summary>The LLM providers the app talks to (references/STACK.md). <see cref="Local"/> is the fine-tuned Matcher on this machine.</summary>
public enum LlmProviderKind { DeepSeek, Claude, OpenAI, Local }

/// <summary>What a model is used for (references/ARQUITETURA.md, section 10: a model per role).</summary>
public enum LlmUseRole
{
    /// <summary>Plans with tools and handles exceptions: the strong model.</summary>
    Planner,

    /// <summary>Recognises a recipe and extracts its values (Recipe Matcher): the cheap model.</summary>
    Matcher,
}

/// <summary>A provider and one of its models.</summary>
public sealed record LlmChoice(LlmProviderKind Provider, string Model);

/// <summary>
/// A completion request. <see cref="Temperature"/> is ignored by the provider when null. <see cref="Provider"/> chooses the
/// adapter when the request goes through <see cref="LlmRouter"/>; null means DeepSeek, as before multi-provider support.
/// </summary>
public sealed record LlmRequest(
    string Model,
    IReadOnlyList<LlmMessage> Messages,
    IReadOnlyList<LlmTool> Tools,
    int MaxTokens,
    double? Temperature = null,
    LlmProviderKind? Provider = null);

public sealed record LlmUsage(int PromptTokens, int CompletionTokens);

public sealed record LlmResponse(
    string? Content,
    IReadOnlyList<LlmToolCall> ToolCalls,
    string? ReasoningContent,
    LlmFinishReason FinishReason,
    LlmUsage Usage)
{
    /// <summary>See <see cref="LlmMessage.ProviderState"/>.</summary>
    public object? ProviderState { get; init; }
}

public enum LlmErrorKind { Unauthorized, RateLimited, Unavailable, InvalidResponse, Timeout, NotConfigured }

/// <summary>Provider failure. The message is generic: it never carries the API key or the response body.</summary>
public sealed class LlmException(LlmErrorKind kind, string message, int? statusCode = null) : Exception(message)
{
    public LlmErrorKind Kind { get; } = kind;
    public int? StatusCode { get; } = statusCode;
}

/// <summary>A chat-completion provider with tool calling.</summary>
public interface ILlmProvider
{
    Task<LlmResponse> CompleteAsync(LlmRequest request, CancellationToken cancellationToken);
}
