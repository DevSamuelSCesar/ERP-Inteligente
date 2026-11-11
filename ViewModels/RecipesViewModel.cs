using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using erp_inteligente.Core.Engine;
using erp_inteligente.Core.Recipes;
using erp_inteligente.Models;
using erp_inteligente.Services;

namespace erp_inteligente.ViewModels;

/// <summary>A row of the recipe list.</summary>
public sealed class RecipeListRow(RecipeRecord record)
{
    public RecipeRecord Record { get; } = record;
    public long Id => Record.Id;
    public string Name => Record.Name;
    public string SkillText => Record.Skill ?? "";
    public string Description => Record.Description;
    public long Uses => Record.SuccessCount;
    public long Failures => Record.FailureCount;
    public string StatusText => RecipesViewModel.StatusLabel(Record.Status);
}

/// <summary>One parameter of the "Usar receita" form.</summary>
public sealed partial class RecipeFieldViewModel(RecipeParameter parameter) : ObservableObject
{
    public RecipeParameter Parameter { get; } = parameter;
    public string Label { get; } = char.ToUpperInvariant(parameter.Label[0]) + parameter.Label[1..];
    public string Hint { get; } = parameter.Example is { Length: > 0 } example ? $"ex.: {example}" : "";

    [ObservableProperty]
    public partial string Value { get; set; } = "";
}

/// <summary>
/// Receitas screen (ROADMAP.md, feature 7): plans that already worked, as templates.
/// <list type="bullet">
/// <item>Everybody with <c>stock.read</c> sees the recipes, their steps, uses and failures.</item>
/// <item>With <c>plan.submit</c>, an active recipe is used without the LLM: fill the values, preview (validated and
/// simulated, nothing written) and send. The engine treats it like any plan (critical ones wait for approval).</item>
/// <item>With <c>recipe.manage</c> (the supervisor): promote a candidate with a name and a description, suspend,
/// reactivate (revalidation) and demote.</item>
/// </list>
/// </summary>
public sealed partial class RecipesViewModel : PageViewModel
{
    private readonly UserSession _user;
    private readonly IRecipeService _recipes;
    private readonly IPlanService _plans;
    private readonly IDialogService _dialogs;
    private IReadOnlyList<RecipeRecord> _all = [];
    private PlanDraft? _previewedDraft;

    public RecipesViewModel(UserSession user, IRecipeService recipes, IPlanService plans, IDialogService dialogs)
    {
        _user = user;
        _recipes = recipes;
        _plans = plans;
        _dialogs = dialogs;
        CanManage = user.Has(Permissions.RecipeManage);
        CanSubmit = user.Has(Permissions.PlanSubmit);
    }

    public override string Title => "Receitas";

    public bool CanManage { get; }
    public bool CanSubmit { get; }
    public bool IsReadOnly => !CanManage;

    public ObservableCollection<RecipeListRow> Rows { get; } = [];
    public ObservableCollection<RecipeFieldViewModel> Fields { get; } = [];
    public ObservableCollection<string> StepLines { get; } = [];
    public ObservableCollection<PlanIssue> Issues { get; } = [];
    public bool IsEmpty => Rows.Count == 0;
    public bool HasIssues => Issues.Count > 0;

    // ---- list ---------------------------------------------------------------------------------------------

    /// <summary>0 = active, 1 = candidates, 2 = suspended or demoted.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowActive), nameof(ShowCandidates), nameof(ShowInactive))]
    public partial int Tab { get; set; }

    public bool ShowActive { get => Tab == 0; set { if (value) Tab = 0; } }
    public bool ShowCandidates { get => Tab == 1; set { if (value) Tab = 1; } }
    public bool ShowInactive { get => Tab == 2; set { if (value) Tab = 2; } }

    partial void OnTabChanged(int value) => ApplyFilter();

    [ObservableProperty]
    public partial string FilterText { get; set; } = "";

    partial void OnFilterTextChanged(string value) => ApplyFilter();

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSelection), nameof(SelectedMeta), nameof(SelectedReason), nameof(HasSelectedReason),
        nameof(CanUseSelected), nameof(CanPromoteSelected), nameof(CanSuspendSelected), nameof(CanReactivateSelected), nameof(CanDemoteSelected))]
    [NotifyCanExecuteChangedFor(nameof(PreviewCommand), nameof(StartPromotionCommand), nameof(SuspendCommand),
        nameof(ReactivateCommand), nameof(DemoteCommand))]
    public partial RecipeListRow? SelectedRow { get; set; }

    partial void OnSelectedRowChanged(RecipeListRow? value)
    {
        Fields.Clear();
        StepLines.Clear();
        ClearPreview();
        if (value is null) return;
        foreach (var parameter in value.Record.Parameters)
            Fields.Add(new RecipeFieldViewModel(parameter));
        var number = 1;
        foreach (var op in value.Record.Operations)
            StepLines.Add($"{number++}. " + (OperationCatalog.TryGet(op, out var d) ? d.Label : op));
    }

    public bool HasSelection => SelectedRow is not null;

    public string SelectedMeta
    {
        get
        {
            if (SelectedRow?.Record is not { } r) return "";
            var parts = new List<string> { StatusLabel(r.Status) };
            if (r.Skill is not null) parts.Add($"skill {r.Skill} ({r.SkillVersion})");
            parts.Add($"usada {r.SuccessCount}×, {r.FailureCount} falha(s)");
            if (r.CreatedAt is not null) parts.Add($"criada em {MemoriesViewModel.DateText(r.CreatedAt)}");
            if (r.PromotedByName is not null) parts.Add($"promovida por {r.PromotedByName}");
            if (r.SourcePlanId is { } plan) parts.Add($"origem: plano {plan}");
            return string.Join(" · ", parts);
        }
    }

    public string? SelectedReason => SelectedRow?.Record.StatusReason;
    public bool HasSelectedReason => !string.IsNullOrEmpty(SelectedReason);

    public bool CanUseSelected => CanSubmit && SelectedRow?.Record.Status == RecipeStatus.Active;
    public bool CanPromoteSelected => CanManage && SelectedRow?.Record.Status == RecipeStatus.Candidate;
    public bool CanSuspendSelected => CanManage && SelectedRow?.Record.Status == RecipeStatus.Active;
    public bool CanReactivateSelected => CanManage && SelectedRow?.Record.Status is RecipeStatus.Suspended or RecipeStatus.Demoted;
    public bool CanDemoteSelected => CanManage && SelectedRow?.Record.Status is not (null or RecipeStatus.Demoted);

    // ---- feedback, preview, result -----------------------------------------------------------------------

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(PreviewCommand), nameof(SubmitCommand), nameof(SavePromotionCommand), nameof(SuspendCommand),
        nameof(ReactivateCommand), nameof(DemoteCommand))]
    public partial bool IsBusy { get; private set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasFeedback), nameof(FeedbackIsSuccess))]
    public partial string Feedback { get; private set; } = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(FeedbackIsSuccess))]
    public partial bool FeedbackIsError { get; private set; }

    public bool HasFeedback => Feedback.Length > 0;
    public bool FeedbackIsSuccess => HasFeedback && !FeedbackIsError;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasPreview))]
    public partial SummaryViewModel? PreviewEffect { get; private set; }

    [ObservableProperty] public partial string? PreviewRiskText { get; private set; }
    [ObservableProperty] public partial bool PreviewIsCritical { get; private set; }
    public bool HasPreview => PreviewEffect is not null || PreviewRiskText is not null;

    [ObservableProperty]
    public partial PlanBoardViewModel? Result { get; private set; }

    // ---- promotion editor --------------------------------------------------------------------------------

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsViewing))]
    [NotifyCanExecuteChangedFor(nameof(SavePromotionCommand), nameof(StartPromotionCommand))]
    public partial bool IsPromoting { get; private set; }

    public bool IsViewing => !IsPromoting;

    [ObservableProperty] public partial string PromoteName { get; set; } = "";
    [ObservableProperty] public partial string PromoteDescription { get; set; } = "";

    // ---- loading -----------------------------------------------------------------------------------------

    public override async Task OnActivatedAsync()
    {
        if (!IsPromoting)
            await ReloadAsync(SelectedRow?.Id);
    }

    private async Task ReloadAsync(long? select)
    {
        try
        {
            _all = await _recipes.ListAsync();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Trace.TraceError("Recipe list failed: {0}", ex.GetType().Name);
            SetFeedback("Não foi possível carregar as receitas.", isError: true);
            _all = [];
        }
        ApplyFilter(select);
    }

    private void ApplyFilter(long? select = null)
    {
        select ??= SelectedRow?.Id;
        var filter = FilterText.Trim();
        var rows = _all
            .Where(r => Tab switch
            {
                0 => r.Status == RecipeStatus.Active,
                1 => r.Status == RecipeStatus.Candidate,
                _ => r.Status is RecipeStatus.Suspended or RecipeStatus.Demoted,
            })
            .Where(r => filter.Length == 0
                        || r.Name.Contains(filter, StringComparison.CurrentCultureIgnoreCase)
                        || r.Description.Contains(filter, StringComparison.CurrentCultureIgnoreCase)
                        || (r.Skill ?? "").Contains(filter, StringComparison.CurrentCultureIgnoreCase))
            .Select(r => new RecipeListRow(r))
            .ToList();
        Rows.Clear();
        foreach (var row in rows) Rows.Add(row);
        SelectedRow = Rows.FirstOrDefault(r => r.Id == select) ?? Rows.FirstOrDefault();
        OnPropertyChanged(nameof(IsEmpty));
    }

    // ---- use ---------------------------------------------------------------------------------------------

    private bool CanPreview() => !IsBusy && CanUseSelected;

    [RelayCommand(CanExecute = nameof(CanPreview))]
    private async Task PreviewAsync()
    {
        if (SelectedRow?.Record is not { } recipe) return;
        ClearPreview();
        var parameters = new JsonObject();
        foreach (var field in Fields)
        {
            if (!RecipeTemplate.TryConvert(field.Parameter, field.Value, out var value, out var error))
            {
                Issues.Add(new PlanIssue(null, error!));
                continue;
            }
            parameters[field.Parameter.Name] = value;
        }
        if (Issues.Count > 0)
        {
            OnPropertyChanged(nameof(HasIssues));
            return;
        }

        IsBusy = true;
        try
        {
            var instance = await _recipes.BuildAsync(recipe.Id, parameters);
            if (!instance.IsValid)
            {
                foreach (var error in instance.Errors) Issues.Add(new PlanIssue(null, error));
                OnPropertyChanged(nameof(HasIssues));
                return;
            }
            var draft = PlanDraft.FromJson(instance.PlanJson!, PlanOrigin.Recipe, recipe.Id);
            var preview = await _plans.PreviewAsync(draft);
            foreach (var issue in preview.Issues) Issues.Add(issue);
            OnPropertyChanged(nameof(HasIssues));
            if (!preview.IsValid) return;

            PreviewEffect = preview.Effect is { } effect ? new SummaryViewModel(effect) : null;
            PreviewIsCritical = preview.Risk == PlanRisk.Critical;
            PreviewRiskText = PreviewIsCritical
                ? "Crítico: depois de enviado, o plano espera a aprovação de outro supervisor."
                : "Reversível: ao enviar, o plano é executado na hora.";
            OnPropertyChanged(nameof(HasPreview));
            _previewedDraft = draft;
            SubmitCommand.NotifyCanExecuteChanged();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Trace.TraceError("Recipe preview failed: {0}", ex.GetType().Name);
            Issues.Add(new PlanIssue(null, "Não foi possível pré-visualizar o plano. Tente de novo."));
            OnPropertyChanged(nameof(HasIssues));
        }
        finally
        {
            IsBusy = false;
        }
    }

    private bool CanSend() => !IsBusy && _previewedDraft is not null;

    [RelayCommand(CanExecute = nameof(CanSend))]
    private async Task SubmitAsync()
    {
        if (_previewedDraft is not { } draft) return;
        IsBusy = true;
        try
        {
            var plan = await _plans.SubmitAsync(draft);
            Result?.Dispose();
            Result = new PlanBoardViewModel(plan, _user, _plans, _dialogs);
            ClearPreview();
            foreach (var field in Fields) field.Value = "";
            await ReloadAsync(SelectedRow?.Id);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Trace.TraceError("Recipe plan submission failed: {0}", ex.GetType().Name);
            Issues.Add(new PlanIssue(null, "Não foi possível enviar o plano. Nada foi gravado. Tente de novo."));
            OnPropertyChanged(nameof(HasIssues));
        }
        finally
        {
            IsBusy = false;
        }
    }

    // ---- supervisor actions ------------------------------------------------------------------------------

    private bool CanStartPromotion() => CanPromoteSelected && !IsPromoting;

    [RelayCommand(CanExecute = nameof(CanStartPromotion))]
    private void StartPromotion()
    {
        var r = SelectedRow!.Record;
        PromoteName = r.Name;
        PromoteDescription = "";
        SetFeedback("", isError: false);
        IsPromoting = true;
    }

    private bool CanSavePromotion() => IsPromoting && !IsBusy;

    [RelayCommand(CanExecute = nameof(CanSavePromotion))]
    private async Task SavePromotionAsync()
    {
        var id = SelectedRow!.Id;
        IsBusy = true;
        try
        {
            await _recipes.PromoteAsync(id, PromoteName, PromoteDescription);
            IsPromoting = false;
            SetFeedback("Receita promovida. Ela já pode ser usada aqui e pelo Nexus.", isError: false);
            Tab = 0;
            await ReloadAsync(id);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            SetFeedback(MessageFor(ex), isError: true);
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private void CancelPromotion()
    {
        if (!IsPromoting) return;
        IsPromoting = false;
        SetFeedback("", isError: false);
    }

    private bool CanSuspend() => !IsBusy && !IsPromoting && CanSuspendSelected;

    [RelayCommand(CanExecute = nameof(CanSuspend))]
    private Task SuspendAsync() => RunAsync(id => _recipes.SuspendAsync(id), "Receita suspensa. Ela não é usada até ser reativada.");

    private bool CanReactivate() => !IsBusy && !IsPromoting && CanReactivateSelected;

    [RelayCommand(CanExecute = nameof(CanReactivate))]
    private Task ReactivateAsync() => RunAsync(id => _recipes.ReactivateAsync(id), "Receita revalidada e ativa de novo.");

    private bool CanDemote() => !IsBusy && !IsPromoting && CanDemoteSelected;

    // No confirmation dialog: demoting is undone by Reactivate, and the line below says so.
    [RelayCommand(CanExecute = nameof(CanDemote))]
    private Task DemoteAsync() =>
        RunAsync(id => _recipes.DemoteAsync(id), "Receita despromovida. Ela fica no histórico e volta só se for reativada.");

    private async Task RunAsync(Func<long, Task> action, string done)
    {
        var id = SelectedRow!.Id;
        IsBusy = true;
        try
        {
            await action(id);
            SetFeedback(done, isError: false);
            await ReloadAsync(id);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            SetFeedback(MessageFor(ex), isError: true);
        }
        finally
        {
            IsBusy = false;
        }
    }

    // ---- helpers -----------------------------------------------------------------------------------------

    private void ClearPreview()
    {
        Issues.Clear();
        OnPropertyChanged(nameof(HasIssues));
        PreviewEffect = null;
        PreviewRiskText = null;
        PreviewIsCritical = false;
        OnPropertyChanged(nameof(HasPreview));
        _previewedDraft = null;
        SubmitCommand.NotifyCanExecuteChanged();
    }

    private void SetFeedback(string text, bool isError)
    {
        FeedbackIsError = isError && text.Length > 0;
        Feedback = text;
    }

    internal static string MessageFor(Exception ex) => ex switch
    {
        Core.Policy.PolicyDeniedException or UnauthorizedAccessException => "Seu papel não permite gerir receitas.",
        RecipeStoreException => ex.Message,
        _ => "Falha ao gravar a receita. Nada foi alterado.",
    };

    internal static string StatusLabel(RecipeStatus status) => status switch
    {
        RecipeStatus.Active => "Ativa",
        RecipeStatus.Candidate => "Candidata",
        RecipeStatus.Suspended => "Suspensa",
        _ => "Despromovida",
    };
}
