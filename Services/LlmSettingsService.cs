using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using erp_inteligente.Core.Audit;
using erp_inteligente.Core.Llm;
using erp_inteligente.Core.Policy;
using erp_inteligente.Models;

namespace erp_inteligente.Services;

/// <summary>
/// The LLM provider settings seen from the Settings screen. Only an administrator (<c>user.manage</c>, checked live)
/// changes the key or the model; every change is audited without the value. The key never leaves the OS key store
/// except to authenticate a request.
/// </summary>
public sealed class LlmSettingsService(LlmSettings settings, PolicyEngine policy, IAuditLog audit, Func<CancellationToken, Task> testConnection)
{
    public const string ProviderName = "DeepSeek";

    public IReadOnlyList<string> Models => LlmSettings.SupportedModels;

    public bool IsConfigured => settings.IsConfigured;

    public string Model => settings.Model;

    public bool CanConfigure(UserSession user) => policy.IsAllowed(user, Permissions.UserManage);

    /// <exception cref="PolicyDeniedException">The user is not an administrator (the denial is audited).</exception>
    /// <exception cref="ArgumentException">The key is malformed; the message never contains it.</exception>
    public void SaveKey(UserSession user, string key)
    {
        policy.Require(user, Permissions.UserManage);
        settings.SetKey(key);
        Audit(user, "llm.key.set");
    }

    /// <exception cref="PolicyDeniedException">The user is not an administrator (the denial is audited).</exception>
    public void ClearKey(UserSession user)
    {
        policy.Require(user, Permissions.UserManage);
        settings.ClearKey();
        Audit(user, "llm.key.clear");
    }

    /// <exception cref="PolicyDeniedException">The user is not an administrator (the denial is audited).</exception>
    /// <exception cref="ArgumentException">The model is not in <see cref="Models"/>.</exception>
    public void SetModel(UserSession user, string model)
    {
        policy.Require(user, Permissions.UserManage);
        if (settings.Model == model) return;
        settings.Model = model;
        Audit(user, "llm.model.set", model);
    }

    /// <summary>Makes a minimal call to the provider. Returns null on success, otherwise a Portuguese message.</summary>
    public async Task<string?> TestAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            await testConnection(cancellationToken);
            return null;
        }
        catch (LlmException ex)
        {
            return ex.Kind switch
            {
                LlmErrorKind.NotConfigured => "Nenhuma chave configurada.",
                LlmErrorKind.Unauthorized => "A DeepSeek recusou a chave.",
                LlmErrorKind.RateLimited => "Limite de uso da DeepSeek atingido. Tente mais tarde.",
                LlmErrorKind.Timeout => "A DeepSeek não respondeu a tempo.",
                LlmErrorKind.Unavailable => "Não foi possível falar com a DeepSeek. Verifique a conexão.",
                _ => "A DeepSeek devolveu uma resposta inválida.",
            };
        }
    }

    private void Audit(UserSession user, string action, string? model = null) =>
        audit.Append(new AuditEntry(AuditActorType.User, user.UserId, null, action, "llm:deepseek", AuditOutcome.Allowed,
            model is null ? null : new System.Text.Json.Nodes.JsonObject { ["model"] = model }.ToJsonString()));
}
