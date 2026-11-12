using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using erp_inteligente.Core.Llm;
using erp_inteligente.Core.Policy;
using erp_inteligente.Models;
using erp_inteligente.Services;

namespace erp_inteligente.ViewModels;

/// <summary>A provider in a combo box.</summary>
public sealed record ProviderOption(LlmProviderKind Kind, string Label)
{
    public override string ToString() => Label;
}

/// <summary>One line of the key status list: provider, whether its key is in the key store.</summary>
public sealed record ProviderStatusRow(string Name, bool IsConfigured, string StatusText);

/// <summary>One backup file in the list.</summary>
public sealed record BackupRow(string FileName, string CreatedText, string SizeText);

/// <summary>An embeddings choice of the RAG search; <see cref="Choice"/> null = off.</summary>
public sealed record EmbeddingOption(LlmChoice? Choice, string Label)
{
    public override string ToString() => Label;
}

/// <summary>
/// Session and application information, plus the LLM section (feature 8):
/// <list type="bullet">
/// <item>the key state of DeepSeek, Claude and OpenAI; an administrator types a key for the chosen provider (kept in the
/// OS key store, never shown again) and tests it;</item>
/// <item>the provider and model of each role (Planner and Recipe Matcher; the Matcher may use the local fine-tuned model);</item>
/// <item>the port of the local model server (loopback only).</item>
/// </list>
/// Everyone else only sees the state. The DeepSeek members of before (<see cref="SelectedModel"/>, <see cref="Models"/>)
/// remain as the Planner's.
/// </summary>
public sealed partial class SettingsViewModel : PageViewModel
{
    private readonly UserSession _user;
    private readonly LlmSettingsService? _llm;
    private readonly IOperationsService? _operations;
    private bool _loading;

    /// <param name="operations">Backups (feature 9); the section is shown to administrators only.</param>
    public SettingsViewModel(UserSession user, string databaseDescription, LlmSettingsService? llm = null, IOperationsService? operations = null)
    {
        _user = user;
        _llm = llm;
        _operations = operations;
        HasBackups = operations is not null && user.Has(Permissions.UserManage);
        BackupDirectory = operations?.BackupDirectory ?? "";
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
        KeyProviders = LlmSettings.KeyedProviders.Select(Option).ToList();
        PlannerProviders = LlmSettings.ProvidersFor(LlmUseRole.Planner).Select(Option).ToList();
        MatcherProviders = LlmSettings.ProvidersFor(LlmUseRole.Matcher).Select(Option).ToList();
        _loading = true;
        SelectedKeyProvider = KeyProviders[0];
        if (llm is not null)
        {
            var planner = llm.ChoiceFor(LlmUseRole.Planner);
            PlannerProvider = PlannerProviders.First(p => p.Kind == planner.Provider);
            PlannerModels = LlmSettings.ModelsFor(planner.Provider);
            PlannerModel = planner.Model;
            var matcher = llm.ChoiceFor(LlmUseRole.Matcher);
            MatcherProvider = MatcherProviders.First(p => p.Kind == matcher.Provider);
            MatcherModels = LlmSettings.ModelsFor(matcher.Provider);
            MatcherModel = matcher.Model;
            LocalPortText = llm.LocalPort.ToString(CultureInfo.InvariantCulture);
            HasEmbeddings = llm.HasEmbeddings;
            var embedding = llm.EmbeddingChoice;
            SelectedEmbedding = EmbeddingOptions.First(o => o.Choice == embedding);
        }
        _loading = false;
        RefreshStatus();
    }

    private static ProviderOption Option(LlmProviderKind kind) => new(kind, LlmSettings.ProviderName(kind));

    public override string Title => "Configurações";

    public IReadOnlyList<SummaryTotal> Fields { get; }

    public bool HasLlm { get; }

    /// <summary>Only an administrator (<c>user.manage</c>) edits the provider settings.</summary>
    public bool CanConfigure { get; }

    public bool IsReadOnlyLlm => HasLlm && !CanConfigure;

    public string ProviderName => LlmSettingsService.ProviderName;

    public ObservableCollection<ProviderStatusRow> ProviderRows { get; } = [];

    // ---- keys ----------------------------------------------------------------------------------------------

    public IReadOnlyList<ProviderOption> KeyProviders { get; }

    [ObservableProperty]
    public partial ProviderOption SelectedKeyProvider { get; set; }

    partial void OnSelectedKeyProviderChanged(ProviderOption value)
    {
        ApiKey = "";
        RefreshStatus();
    }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SaveKeyCommand), nameof(TestCommand), nameof(ClearKeyCommand))]
    public partial bool IsKeyConfigured { get; private set; }

    [ObservableProperty] public partial string KeyStatusText { get; private set; } = "";

    /// <summary>The key being typed; cleared as soon as it is saved.</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SaveKeyCommand))]
    public partial string ApiKey { get; set; } = "";

    // ---- models per role -----------------------------------------------------------------------------------

    public IReadOnlyList<ProviderOption> PlannerProviders { get; }
    public IReadOnlyList<ProviderOption> MatcherProviders { get; }

    [ObservableProperty] public partial ProviderOption? PlannerProvider { get; set; }
    [ObservableProperty] public partial IReadOnlyList<string> PlannerModels { get; private set; } = [];
    [ObservableProperty] public partial string? PlannerModel { get; set; }

    [ObservableProperty] public partial ProviderOption? MatcherProvider { get; set; }
    [ObservableProperty] public partial IReadOnlyList<string> MatcherModels { get; private set; } = [];
    [ObservableProperty] public partial string? MatcherModel { get; set; }

    /// <summary>The Planner's models (DeepSeek's, before feature 8).</summary>
    public IReadOnlyList<string> Models => PlannerModels;

    /// <summary>The Planner's model (kept for the DeepSeek-only screen of before feature 8).</summary>
    public string? SelectedModel
    {
        get => PlannerModel;
        set => PlannerModel = value;
    }

    partial void OnPlannerProviderChanged(ProviderOption? value) => ProviderChanged(LlmUseRole.Planner, value);
    partial void OnMatcherProviderChanged(ProviderOption? value) => ProviderChanged(LlmUseRole.Matcher, value);
    partial void OnPlannerModelChanged(string? value) => ModelChanged(LlmUseRole.Planner, PlannerProvider, value);
    partial void OnMatcherModelChanged(string? value) => ModelChanged(LlmUseRole.Matcher, MatcherProvider, value);

    /// <summary>A new provider offers its own models; the first one is chosen and saved once.</summary>
    private void ProviderChanged(LlmUseRole role, ProviderOption? provider)
    {
        if (_loading || provider is null) return;
        var models = LlmSettings.ModelsFor(provider.Kind);
        _loading = true;
        if (role == LlmUseRole.Planner) { PlannerModels = models; PlannerModel = models[0]; }
        else { MatcherModels = models; MatcherModel = models[0]; }
        _loading = false;
        OnPropertyChanged(nameof(Models));
        Save(role, new LlmChoice(provider.Kind, models[0]));
    }

    private void ModelChanged(LlmUseRole role, ProviderOption? provider, string? model)
    {
        if (_loading || provider is null || model is null) return;
        Save(role, new LlmChoice(provider.Kind, model));
    }

    private void Save(LlmUseRole role, LlmChoice choice)
    {
        if (_llm is null || !CanConfigure || _llm.ChoiceFor(role) == choice) return;
        var label = role == LlmUseRole.Planner ? "Planejamento" : "Reconhecimento de receitas";
        // Fire and forget: Run catches every failure and reports it in Feedback.
        _ = Run(() =>
        {
            _llm.SetChoice(_user, role, choice);
            return Task.FromResult<string?>(null);
        }, $"{label}: {LlmSettings.ProviderName(choice.Provider)}, modelo {choice.Model}.");
    }

    // ---- embeddings of the RAG (feature 5) -------------------------------------------------------------------

    /// <summary>Off (BM25 only), OpenAI text-embedding-3-small or the model served on 127.0.0.1.</summary>
    public IReadOnlyList<EmbeddingOption> EmbeddingOptions { get; } =
    [
        new(null, "Desligada (só palavras)"),
        new(new LlmChoice(LlmProviderKind.OpenAI, LlmSettings.OpenAiEmbeddingModel), "OpenAI · " + LlmSettings.OpenAiEmbeddingModel),
        new(new LlmChoice(LlmProviderKind.Local, LlmSettings.LocalEmbeddingModel), "Modelo local · " + LlmSettings.LocalEmbeddingModel),
    ];

    public bool HasEmbeddings { get; }

    [ObservableProperty]
    public partial EmbeddingOption? SelectedEmbedding { get; set; }

    [ObservableProperty] public partial string EmbeddingStatusText { get; private set; } = "";

    /// <summary>Saving a new provider indexes the procedures right away (only chunks without a vector of that model).</summary>
    partial void OnSelectedEmbeddingChanged(EmbeddingOption? value)
    {
        if (_loading || value is null || _llm is null || !CanConfigure || _llm.EmbeddingChoice == value.Choice) return;
        _ = Run(async () =>
        {
            _llm.SetEmbeddingChoice(_user, value.Choice);
            return value.Choice is null ? null : await _llm.IndexAsync(_user);
        }, value.Choice is null ? "Busca por significado desligada: só palavras (BM25)." : $"Busca híbrida com {value.Label}: procedimentos indexados.");
    }

    private bool CanIndex() => CanConfigure && !IsWorking && SelectedEmbedding?.Choice is not null;

    [RelayCommand(CanExecute = nameof(CanIndex))]
    private Task IndexAsync() => Run(() => _llm!.IndexAsync(_user), "Procedimentos indexados.");

    // ---- local model ---------------------------------------------------------------------------------------

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SaveLocalPortCommand))]
    public partial string LocalPortText { get; set; } = "";

    // ---- backups (feature 9) -------------------------------------------------------------------------------

    /// <summary>Administrators manage backups: copies encrypted with this machine's key, in a folder next to the database.</summary>
    public bool HasBackups { get; }

    public string BackupDirectory { get; }

    public ObservableCollection<BackupRow> Backups { get; } = [];

    public bool HasNoBackups => Backups.Count == 0;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RequestRestoreCommand))]
    public partial BackupRow? SelectedBackup { get; set; }

    partial void OnSelectedBackupChanged(BackupRow? value) => PendingRestore = null;

    /// <summary>The backup the administrator asked to restore; the second click confirms.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsConfirmingRestore), nameof(RestoreQuestion))]
    [NotifyCanExecuteChangedFor(nameof(ConfirmRestoreCommand))]
    public partial BackupRow? PendingRestore { get; private set; }

    public bool IsConfirmingRestore => PendingRestore is not null;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasBackupFeedback))]
    public partial string BackupFeedback { get; private set; } = "";

    [ObservableProperty] public partial bool BackupFeedbackIsError { get; private set; }

    public bool HasBackupFeedback => BackupFeedback.Length > 0;

    public string RestoreQuestion => PendingRestore is null ? "" :
        $"Restaurar {PendingRestore.FileName} ({PendingRestore.CreatedText})? Tudo o que foi feito depois dele some do banco. " +
        "Antes, o estado atual é salvo num backup novo.";

    public override async Task OnActivatedAsync()
    {
        if (HasBackups) await LoadBackupsAsync();
    }

    private async Task LoadBackupsAsync()
    {
        try
        {
            var list = await _operations!.ListBackupsAsync();
            Backups.Clear();
            foreach (var b in list)
                Backups.Add(new BackupRow(b.FileName, b.CreatedAt.ToLocalTime().ToString("dd/MM/yyyy HH:mm", CultureInfo.InvariantCulture),
                    (b.SizeBytes / 1024.0).ToString("N0", CultureInfo.GetCultureInfo("pt-BR")) + " KB"));
            OnPropertyChanged(nameof(HasNoBackups));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Trace.TraceError("Backup list failed: {0}", ex.GetType().Name);
        }
    }

    private bool CanCreateBackup() => HasBackups && !IsWorking;

    [RelayCommand(CanExecute = nameof(CanCreateBackup))]
    private async Task CreateBackupAsync()
    {
        await Run(async () =>
        {
            await _operations!.CreateBackupAsync();
            return (string?)null;
        }, "Backup criado e verificado. Ele só abre com a chave deste computador.", backup: true);
        await LoadBackupsAsync();
    }

    private bool CanRequestRestore() => HasBackups && !IsWorking && SelectedBackup is not null;

    [RelayCommand(CanExecute = nameof(CanRequestRestore))]
    private void RequestRestore() => PendingRestore = SelectedBackup;

    [RelayCommand]
    private void CancelRestore() => PendingRestore = null;

    private bool CanConfirmRestore() => HasBackups && !IsWorking && PendingRestore is not null;

    [RelayCommand(CanExecute = nameof(CanConfirmRestore))]
    private async Task ConfirmRestoreAsync()
    {
        var name = PendingRestore!.FileName;
        PendingRestore = null;
        await Run(async () =>
        {
            await _operations!.RestoreBackupAsync(name);
            return (string?)null;
        }, $"Banco restaurado de {name}. Saia e entre de novo para ver os dados restaurados.", backup: true);
        await LoadBackupsAsync();
    }

    // ---- commands ------------------------------------------------------------------------------------------

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SaveKeyCommand), nameof(ClearKeyCommand), nameof(TestCommand), nameof(SaveLocalPortCommand), nameof(TestLocalCommand),
        nameof(CreateBackupCommand), nameof(RequestRestoreCommand), nameof(ConfirmRestoreCommand), nameof(IndexCommand))]
    public partial bool IsWorking { get; private set; }

    [ObservableProperty] public partial string Feedback { get; private set; } = "";
    [ObservableProperty] public partial bool FeedbackIsError { get; private set; }
    [ObservableProperty] public partial bool FeedbackIsSuccess { get; private set; }

    private bool CanSaveKey() => CanConfigure && !IsWorking && !string.IsNullOrWhiteSpace(ApiKey);

    [RelayCommand(CanExecute = nameof(CanSaveKey))]
    private Task SaveKeyAsync()
    {
        var key = ApiKey;
        var provider = SelectedKeyProvider.Kind;
        ApiKey = "";
        return Run(() =>
        {
            _llm!.SaveKey(_user, provider, key);
            return Task.FromResult<string?>(null);
        }, $"Chave da {LlmSettings.ProviderName(provider)} salva no cofre do sistema.");
    }

    private bool CanClearKey() => CanConfigure && !IsWorking && IsKeyConfigured;

    [RelayCommand(CanExecute = nameof(CanClearKey))]
    private Task ClearKeyAsync()
    {
        var provider = SelectedKeyProvider.Kind;
        return Run(() =>
        {
            _llm!.ClearKey(_user, provider);
            return Task.FromResult<string?>(null);
        }, $"Chave da {LlmSettings.ProviderName(provider)} removida.");
    }

    private bool CanTest() => CanConfigure && !IsWorking && IsKeyConfigured;

    [RelayCommand(CanExecute = nameof(CanTest))]
    private Task TestAsync()
    {
        var provider = SelectedKeyProvider.Kind;
        return Run(() => _llm!.TestAsync(provider), $"Conexão com a {LlmSettings.ProviderName(provider)} funcionando.");
    }

    private bool CanSaveLocalPort() => CanConfigure && !IsWorking && LocalPortText.Trim().Length > 0;

    [RelayCommand(CanExecute = nameof(CanSaveLocalPort))]
    private Task SaveLocalPortAsync()
    {
        if (!int.TryParse(LocalPortText.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var port) || port is < 1024 or > 65535)
        {
            Feedback = "A porta deve ser um número de 1024 a 65535.";
            FeedbackIsError = true;
            FeedbackIsSuccess = false;
            return Task.CompletedTask;
        }
        return Run(() =>
        {
            _llm!.SetLocalPort(_user, port);
            return Task.FromResult<string?>(null);
        }, $"Porta do modelo local: {port} (somente 127.0.0.1).");
    }

    private bool CanTestLocal() => CanConfigure && !IsWorking;

    [RelayCommand(CanExecute = nameof(CanTestLocal))]
    private Task TestLocalAsync() => Run(() => _llm!.TestAsync(LlmProviderKind.Local), "Modelo local respondendo.");

    /// <summary>Runs a settings action off the UI thread; the work returns an error message or null on success.</summary>
    /// <param name="backup">Reports in the backup section instead of the LLM section.</param>
    private async Task Run(Func<Task<string?>> work, string success, bool backup = false)
    {
        IsWorking = true;
        Feedback = "";
        BackupFeedback = "";
        FeedbackIsError = FeedbackIsSuccess = false;
        string? error;
        try
        {
            error = await Task.Run(work);
        }
        catch (ArgumentException)
        {
            error = "Valor inválido: a chave deve ter de 8 a 200 caracteres, sem espaços.";
        }
        catch (Core.Operations.BackupException ex)
        {
            error = ex.Message;
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

        if (backup)
        {
            BackupFeedback = error ?? success;
            BackupFeedbackIsError = error is not null;
        }
        else
        {
            Feedback = error ?? success;
            FeedbackIsError = error is not null;
            FeedbackIsSuccess = error is null;
        }
        RefreshStatus();
    }

    private void RefreshStatus()
    {
        if (_llm is null)
            return;
        var rows = new List<ProviderStatusRow>();
        bool selectedConfigured = false;
        foreach (var option in KeyProviders)
        {
            bool configured;
            try
            {
                configured = _llm.IsConfiguredFor(option.Kind);
            }
            catch (Exception ex)
            {
                Trace.TraceError("Key store unavailable: {0}", ex.GetType().Name);
                configured = false;
            }
            var uses = new List<string>();
            if (PlannerProvider?.Kind == option.Kind) uses.Add($"planejamento ({PlannerModel})");
            if (MatcherProvider?.Kind == option.Kind) uses.Add($"receitas ({MatcherModel})");
            var text = (configured ? "Chave configurada" : "Sem chave") + (uses.Count > 0 ? " · usada em " + string.Join(" e ", uses) : "");
            rows.Add(new ProviderStatusRow(option.Label, configured, text));
            if (option.Kind == SelectedKeyProvider.Kind) selectedConfigured = configured;
        }
        ProviderRows.Clear();
        foreach (var row in rows) ProviderRows.Add(row);
        IndexCommand.NotifyCanExecuteChanged();
        try
        {
            EmbeddingStatusText = _llm.Coverage() is not { } coverage ? ""
                : coverage.Model is null ? "Desligada: os procedimentos são buscados só por palavras (BM25)."
                : $"{coverage.WithVector} de {coverage.Total} trechos com vetor · " +
                  (coverage.SqliteVec ? "distância calculada pelo sqlite-vec" : "distância calculada no app") +
                  " · palavras e significado combinados (RRF)";
        }
        catch (Exception ex)
        {
            Trace.TraceError("Vector coverage unavailable: {0}", ex.GetType().Name);
            EmbeddingStatusText = "";
        }

        IsKeyConfigured = selectedConfigured;
        var name = SelectedKeyProvider.Label;
        KeyStatusText = selectedConfigured
            ? $"Chave configurada · {name}" + (PlannerProvider?.Kind == SelectedKeyProvider.Kind ? $" · modelo {PlannerModel}" : "")
            : CanConfigure ? $"Sem chave da {name}: os papéis que a usam não funcionam até uma chave ser salva." : "Sem chave: peça ao administrador.";
    }
}
