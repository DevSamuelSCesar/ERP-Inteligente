using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace erp_inteligente.Core.Llm;

/// <summary>
/// Sends each request to the adapter of <see cref="LlmRequest.Provider"/> (DeepSeek when null), so the Planner and the
/// Matcher can use different providers (references/ARQUITETURA.md, section 10). A provider without an adapter is a
/// configuration error, reported as <see cref="LlmErrorKind.NotConfigured"/>.
/// </summary>
public sealed class LlmRouter(IReadOnlyDictionary<LlmProviderKind, ILlmProvider> providers) : ILlmProvider
{
    public Task<LlmResponse> CompleteAsync(LlmRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var kind = request.Provider ?? LlmProviderKind.DeepSeek;
        return providers.TryGetValue(kind, out var provider)
            ? provider.CompleteAsync(request, cancellationToken)
            : Task.FromException<LlmResponse>(new LlmException(LlmErrorKind.NotConfigured, "No adapter for the chosen LLM provider."));
    }
}
