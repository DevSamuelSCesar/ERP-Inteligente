using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using erp_inteligente.Core.Policy;
using erp_inteligente.Models;
using erp_inteligente.Services;

namespace erp_inteligente.ViewModels;

/// <summary>
/// Session and application information, plus the LLM provider section: an administrator types the DeepSeek key
/// (kept in the OS key store, never shown again) and picks the model; everyone else only sees whether it is set.
/// </summary>
public sealed partial class SettingsViewModel : PageViewModel
{
    private readonly UserSession _user;
    private readonly LlmSettingsService? _llm;

    public SettingsViewModel(UserSession user, string databaseDescription, LlmSettingsService? llm = null)
    {
        _user = user;
        _llm = llm;
        Fields =
        [
            new("Usuário", $"{user.DisplayName} ({user.Username})"),
            new("Papel", user.RoleName),
            new("Armazém", user.WarehouseCode),
            new("Banco de dados", databaseDescription),
            new("Sessão", "Expira após 15 minutos sem uso"),
            new("Tema", "Escuro"),
            new("Fonte", "Barlow (SIL OFL)"),
        ];
        HasLlm = llm is not null;
        CanConfigure = llm?.CanConfigure(user) ?? false;
        Models = llm?.Models ?? [];
        SelectedModel = llm?.Model;
        RefreshStatus();
    }

    public override string Title => "Configurações";

    public IReadOnlyList<SummaryTotal> Fields { get; }

    public bool HasLlm { get; }

    /// <summary>Only an administrator (<c>user.manage</c>) edits the provider settings.</summary>
    public bool CanConfigure { get; }

    public bool IsReadOnlyLlm => HasLlm && !CanConfigure;

    public string ProviderName => LlmSettingsService.ProviderName;

    public IReadOnlyList<string> Models { get; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SaveKeyCommand), nameof(TestCommand))]
    public partial bool IsKeyConfigured { get; private set; }

    [ObservableProperty] public partial string KeyStatusText { get; private set; } = "";

    /// <summary>The key being typed; cleared as soon as it is saved.</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SaveKeyCommand))]
    public partial string ApiKey { get; set; } = "";

    [ObservableProperty] public partial string? SelectedModel { get; set; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SaveKeyCommand), nameof(ClearKeyCommand), nameof(TestCommand))]
    public partial bool IsWorking { get; private set; }

    [ObservableProperty] public partial string Feedback { get; private set; } = "";
    [ObservableProperty] public partial bool FeedbackIsError { get; private set; }
    [ObservableProperty] public partial bool FeedbackIsSuccess { get; private set; }

    partial void OnSelectedModelChanged(string? value)
    {
        if (_llm is null || !CanConfigure || value is null || value == _llm.Model)
            return;
        // Fire and forget: Run catches every failure and reports it in Feedback.
        _ = Run(() =>
        {
            _llm.SetModel(_user, value);
            return Task.FromResult<string?>(null);
        }, $"Modelo alterado para {value}.");
    }

    private bool CanSaveKey() => CanConfigure && !IsWorking && !string.IsNullOrWhiteSpace(ApiKey);

    [RelayCommand(CanExecute = nameof(CanSaveKey))]
    private Task SaveKeyAsync()
    {
        var key = ApiKey;
        ApiKey = "";
        return Run(() =>
        {
            _llm!.SaveKey(_user, key);
            return Task.FromResult<string?>(null);
        }, "Chave salva no cofre do sistema.");
    }

    private bool CanClearKey() => CanConfigure && !IsWorking && IsKeyConfigured;

    [RelayCommand(CanExecute = nameof(CanClearKey))]
    private Task ClearKeyAsync() => Run(() =>
    {
        _llm!.ClearKey(_user);
        return Task.FromResult<string?>(null);
    }, "Chave removida.");

    private bool CanTest() => CanConfigure && !IsWorking && IsKeyConfigured;

    [RelayCommand(CanExecute = nameof(CanTest))]
    private Task TestAsync() => Run(() => _llm!.TestAsync(), "Conexão com a DeepSeek funcionando.");

    /// <summary>Runs a settings action off the UI thread; the work returns an error message or null on success.</summary>
    private async Task Run(Func<Task<string?>> work, string success)
    {
        IsWorking = true;
        Feedback = "";
        FeedbackIsError = FeedbackIsSuccess = false;
        string? error;
        try
        {
            error = await Task.Run(work);
        }
        catch (ArgumentException)
        {
            error = $"Chave inválida: use de 8 a 200 caracteres, sem espaços.";
        }
        catch (PolicyDeniedException)
        {
            error = "Só o administrador altera o provedor de LLM.";
        }
        catch (Exception ex)
        {
            Trace.TraceError("LLM settings action failed: {0}", ex.GetType().Name);
            error = "Não foi possível acessar o cofre do sistema.";
        }
        finally
        {
            IsWorking = false;
        }

        Feedback = error ?? success;
        FeedbackIsError = error is not null;
        FeedbackIsSuccess = error is null;
        RefreshStatus();
    }

    private void RefreshStatus()
    {
        if (_llm is null)
            return;
        bool configured;
        try
        {
            configured = _llm.IsConfigured;
        }
        catch (Exception ex)
        {
            Trace.TraceError("Key store unavailable: {0}", ex.GetType().Name);
            configured = false;
        }
        IsKeyConfigured = configured;
        KeyStatusText = configured
            ? $"Chave configurada · modelo {_llm.Model}"
            : CanConfigure ? "Sem chave: o Nexus não planeja até uma chave ser salva." : "Sem chave: peça ao administrador.";
    }
}
