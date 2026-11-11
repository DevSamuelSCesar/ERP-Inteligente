using System;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace erp_inteligente.Core.Llm;

/// <summary>
/// DeepSeek chat-completions client (OpenAI-compatible, non-streaming). The host is fixed so the key can never be sent
/// elsewhere. In thinking mode, <c>reasoning_content</c> is sent back on the following calls, as the API requires when
/// tools are in use.
/// </summary>
public sealed class DeepSeekProvider : ChatCompletionsProvider
{
    private static readonly Uri Url = new("https://api.deepseek.com/chat/completions");

    /// <param name="handler">HTTP transport (use <see cref="ChatCompletionsProvider.CreateDefaultHandler"/>); not disposed by the provider.</param>
    /// <param name="keySource">Returns a fresh key buffer per call; the provider zeroes it.</param>
    /// <param name="delay">Backoff delay function (injectable for tests).</param>
    /// <param name="timeout">Per-attempt timeout; 60 s by default.</param>
    public DeepSeekProvider(HttpMessageHandler handler, Func<byte[]?> keySource,
        Func<TimeSpan, CancellationToken, Task>? delay = null, TimeSpan? timeout = null)
        : base(handler, keySource ?? throw new ArgumentNullException(nameof(keySource)), delay, timeout)
    {
    }

    protected override Uri Endpoint => Url;

    protected override bool SendsReasoningBack => true;

    /// <summary>Minimal 1-token call with thinking off; throws <see cref="LlmException"/> when the key or service is unusable.</summary>
    public async Task TestConnectionAsync(CancellationToken cancellationToken)
    {
        var probe = new LlmRequest(LlmSettings.FastModel, [LlmMessage.User("ping")], [], 1);
        await SendAsync(BuildBody(probe, w =>
        {
            w.WriteStartObject("thinking");
            w.WriteString("type", "disabled");
            w.WriteEndObject();
        }), cancellationToken).ConfigureAwait(false);
    }
}
