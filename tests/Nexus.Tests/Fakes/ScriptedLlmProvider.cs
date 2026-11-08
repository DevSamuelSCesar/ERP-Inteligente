using erp_inteligente.Core.Llm;

namespace Nexus.Tests.Fakes;

/// <summary>An LLM that replays queued responses and records every request; with nothing queued it answers "ok".</summary>
public sealed class ScriptedLlmProvider : ILlmProvider
{
    private readonly Queue<Func<LlmRequest, LlmResponse>> _script = new();

    public List<LlmRequest> Requests { get; } = [];

    /// <summary>Makes the next call wait for this task, e.g. to observe the busy state or cancel the request.</summary>
    public Task? Gate { get; set; }

    public ScriptedLlmProvider Reply(string text) =>
        Then(_ => new LlmResponse(text, [], null, LlmFinishReason.Stop, new LlmUsage(10, 5)));

    public ScriptedLlmProvider Call(string tool, string argumentsJson) =>
        Then(r => new LlmResponse(null, [new LlmToolCall($"call_{r.Messages.Count}", tool, argumentsJson)], null,
            LlmFinishReason.ToolCalls, new LlmUsage(10, 5)));

    public ScriptedLlmProvider Fail(LlmErrorKind kind) => Then(_ => throw new LlmException(kind, "scripted failure"));

    public ScriptedLlmProvider Then(Func<LlmRequest, LlmResponse> step)
    {
        _script.Enqueue(step);
        return this;
    }

    public async Task<LlmResponse> CompleteAsync(LlmRequest request, CancellationToken cancellationToken)
    {
        Requests.Add(request);
        if (Gate is { } gate)
            await gate.WaitAsync(cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        return _script.Count > 0
            ? _script.Dequeue()(request)
            : new LlmResponse("ok", [], null, LlmFinishReason.Stop, new LlmUsage(1, 1));
    }
}
