using System;
using System.Collections.Generic;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using erp_inteligente.Core.Audit;
using erp_inteligente.Core.Llm;
using erp_inteligente.Core.Policy;
using erp_inteligente.Models;

namespace erp_inteligente.Services;

/// <summary>
/// The LLM settings seen from the Settings screen (feature 8: DeepSeek, Claude, OpenAI and a local model; a model per
/// role). Only an administrator (<c>user.manage</c>, checked live) changes keys, models or the local port; every change is
/// audited without the value of a key. Keys never leave the OS key store except to authenticate a request. The members
/// without a provider argument are DeepSeek's, as before.
/// </summary>
public sealed class LlmSettingsService(
    LlmSettings settings, PolicyEngine policy, IAuditLog audit, Func<LlmProviderKind, CancellationToken, Task> testConnection)
{
    public const string ProviderName = "DeepSeek";

    public IReadOnlyList<string> Models => LlmSettings.SupportedModels;

    public bool IsConfigured => settings.IsConfigured;

    public string Model => settings.Model;

    public bool IsConfiguredFor(LlmProviderKind provider) => settings.IsConfiguredFor(provider);

    public LlmChoice ChoiceFor(LlmUseRole role) => settings.ChoiceFor(role);

    public int LocalPort => settings.LocalPort;

    public bool CanConfigure(UserSession user) => policy.IsAllowed(user, Permissions.UserManage);

    /// <exception cref="PolicyDeniedException">The user is not an administrator (the denial is audited).</exception>
    /// <exception cref="ArgumentException">The key is malformed; the message never contains it.</exception>
    public void SaveKey(UserSession user, string key) => SaveKey(user, LlmProviderKind.DeepSeek, key);

    /// <inheritdoc cref="SaveKey(UserSession, string)"/>
    public void SaveKey(UserSession user, LlmProviderKind provider, string key)
    {
        policy.Require(user, Permissions.UserManage);
        settings.SetKey(provider, key);
        Audit(user, "llm.key.set", provider);
    }

    /// <exception cref="PolicyDeniedException">The user is not an administrator (the denial is audited).</exception>
    public void ClearKey(UserSession user) => ClearKey(user, LlmProviderKind.DeepSeek);

    public void ClearKey(UserSession user, LlmProviderKind provider)
    {
        policy.Require(user, Permissions.UserManage);
        settings.ClearKey(provider);
        Audit(user, "llm.key.clear", provider);
    }

    /// <summary>The DeepSeek planning model (before feature 8 the only choice).</summary>
    /// <exception cref="PolicyDeniedException">The user is not an administrator (the denial is audited).</exception>
    /// <exception cref="ArgumentException">The model is not in <see cref="Models"/>.</exception>
    public void SetModel(UserSession user, string model) => SetChoice(user, LlmUseRole.Planner, new LlmChoice(LlmProviderKind.DeepSeek, model));

    /// <exception cref="PolicyDeniedException">The user is not an administrator (the denial is audited).</exception>
    /// <exception cref="ArgumentException">The model is not offered for the role.</exception>
    public void SetChoice(UserSession user, LlmUseRole role, LlmChoice choice)
    {
        policy.Require(user, Permissions.UserManage);
        if (settings.ChoiceFor(role) == choice) return;
        settings.SetChoice(role, choice);
        Audit(user, "llm.model.set", choice.Provider, new JsonObject
        {
            ["role"] = role == LlmUseRole.Planner ? "planner" : "matcher",
            ["model"] = choice.Model,
        });
    }

    /// <exception cref="PolicyDeniedException">The user is not an administrator (the denial is audited).</exception>
    /// <exception cref="ArgumentException">The port is outside 1024 to 65535.</exception>
    public void SetLocalPort(UserSession user, int port)
    {
        policy.Require(user, Permissions.UserManage);
        if (settings.LocalPort == port) return;
        settings.LocalPort = port;
        Audit(user, "llm.local.port", LlmProviderKind.Local, new JsonObject { ["port"] = port });
    }

    /// <summary>Makes a minimal call to DeepSeek. Returns null on success, otherwise a Portuguese message.</summary>
    public Task<string?> TestAsync(CancellationToken cancellationToken = default) => TestAsync(LlmProviderKind.DeepSeek, cancellationToken);

    /// <summary>Makes a minimal call to the provider. Returns null on success, otherwise a Portuguese message.</summary>
    public async Task<string?> TestAsync(LlmProviderKind provider, CancellationToken cancellationToken = default)
    {
        var name = LlmSettings.ProviderName(provider);
        try
        {
            await testConnection(provider, cancellationToken);
            return null;
        }
        catch (LlmException ex)
        {
            return ex.Kind switch
            {
                LlmErrorKind.NotConfigured => provider == LlmProviderKind.Local ? "A porta do modelo local é inválida." : "Nenhuma chave configurada.",
                LlmErrorKind.Unauthorized => $"{name}: a chave foi recusada.",
                LlmErrorKind.RateLimited => $"{name}: limite de uso atingido. Tente mais tarde.",
                LlmErrorKind.Timeout => $"{name}: não respondeu a tempo.",
                LlmErrorKind.Unavailable => $"{name}: não foi possível conectar. Verifique a conexão" +
                                            (provider == LlmProviderKind.Local ? " e se o servidor local está rodando." : "."),
                _ => $"{name}: resposta inválida.",
            };
        }
    }

    private void Audit(UserSession user, string action, LlmProviderKind provider, JsonObject? details = null) =>
        audit.Append(new AuditEntry(AuditActorType.User, user.UserId, null, action, "llm:" + provider.ToString().ToLowerInvariant(),
            AuditOutcome.Allowed, details?.ToJsonString()));
}
