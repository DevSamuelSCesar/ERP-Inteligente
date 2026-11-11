using System;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace erp_inteligente.Core.Llm;

/// <summary>
/// OpenAI chat-completions client (non-streaming, feature 8). Fixed host, no redirects, key from the OS key store on
/// every call. Current models take <c>max_completion_tokens</c> instead of <c>max_tokens</c>.
/// </summary>
public sealed class OpenAiProvider : ChatCompletionsProvider
{
    private static readonly Uri Url = new("https://api.openai.com/v1/chat/completions");

    public OpenAiProvider(HttpMessageHandler handler, Func<byte[]?> keySource,
        Func<TimeSpan, CancellationToken, Task>? delay = null, TimeSpan? timeout = null)
        : base(handler, keySource ?? throw new ArgumentNullException(nameof(keySource)), delay, timeout)
    {
    }

    protected override Uri Endpoint => Url;

    protected override string MaxTokensField => "max_completion_tokens";

    /// <summary>Minimal call to the cheapest offered model; throws <see cref="LlmException"/> when the key or service is unusable.</summary>
    public async Task TestConnectionAsync(CancellationToken cancellationToken)
    {
        var probe = new LlmRequest(LlmSettings.OpenAiFastModel, [LlmMessage.User("ping")], [], 16);
        await SendAsync(BuildBody(probe, null), cancellationToken).ConfigureAwait(false);
    }
}

/// <summary>
/// The fine-tuned Matcher served on this machine by an OpenAI-compatible server (llama.cpp, vLLM, Ollama...), feature 8.
/// Only loopback is accepted (<c>http://127.0.0.1:port</c>), so nothing leaves the machine; there is no key.
/// </summary>
public sealed class LocalProvider : ChatCompletionsProvider
{
    private readonly Func<int> _port;

    /// <param name="port">Reads the configured port on every call (1024 to 65535).</param>
    public LocalProvider(HttpMessageHandler handler, Func<int> port,
        Func<TimeSpan, CancellationToken, Task>? delay = null, TimeSpan? timeout = null)
        : base(handler, keySource: null, delay, timeout ?? TimeSpan.FromSeconds(30))
    {
        _port = port ?? throw new ArgumentNullException(nameof(port));
    }

    protected override Uri Endpoint
    {
        get
        {
            var port = _port();
            if (port is < 1024 or > 65535)
                throw new LlmException(LlmErrorKind.NotConfigured, "The local model port is not configured.");
            return new Uri($"http://127.0.0.1:{port}/v1/chat/completions");
        }
    }

    public async Task TestConnectionAsync(string model, CancellationToken cancellationToken)
    {
        var probe = new LlmRequest(model, [LlmMessage.User("ping")], [], 8);
        await SendAsync(BuildBody(probe, null), cancellationToken).ConfigureAwait(false);
    }
}
