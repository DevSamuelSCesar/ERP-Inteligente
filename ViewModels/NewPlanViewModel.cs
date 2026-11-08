using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using erp_inteligente.Models;
using erp_inteligente.Services;

namespace erp_inteligente.ViewModels;

public enum PlanFieldKind
{
    Text,
    Integer,
    Decimal,
    Date,
    Flag,
    /// <summary>Order items typed one per line as "SKU quantidade".</summary>
    Lines,
}

/// <summary>One input of the operation form; <see cref="Key"/> is the DSL argument name.</summary>
public sealed partial class PlanFieldViewModel(string key, string label, PlanFieldKind kind, bool required, string hint = "",
    PlanLookup lookup = PlanLookup.None)
    : ObservableObject
{
    public PlanFieldViewModel(PlanFieldSpec spec) : this(spec.Key, spec.Label, spec.Kind, spec.Required, spec.Hint, spec.Lookup) { }

    public string Key { get; } = key;
    public string Label { get; } = required ? label : $"{label} (opcional)";
    public PlanFieldKind Kind { get; } = kind;
    public bool IsRequired { get; } = required;
    public PlanLookup Lookup { get; } = lookup;
    /// <summary>Example shown while the field is empty; prefixed so it is never mistaken for a value.</summary>
    public string Hint { get; } = hint.Length == 0 ? "" : hint.StartsWith("AAAA", StringComparison.Ordinal) || hint.Contains('(') ? hint : $"ex.: {hint}";
    public bool IsFlag => Kind == PlanFieldKind.Flag;
    public bool IsLines => Kind == PlanFieldKind.Lines;
    public bool IsText => Kind is not (PlanFieldKind.Flag or PlanFieldKind.Lines) && Suggestions.Count == 0;
    public bool HasSuggestions => Kind is not (PlanFieldKind.Flag or PlanFieldKind.Lines) && Suggestions.Count > 0;

    /// <summary>Existing values to pick from (SKUs, codes, names); typing anything else is still allowed.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsText), nameof(HasSuggestions))]
    public partial IReadOnlyList<string> Suggestions { get; set; } = [];

    [ObservableProperty]
    public partial string Value { get; set; } = "";

    [ObservableProperty]
    public partial bool Checked { get; set; }
}

/// <summary>An operation the user may plan, with its form fields (mirrors the DSL v1 schemas).</summary>
public sealed record PlanOperationOption(
    string Op, string Label, string Permission, string Intent, string Skill, IReadOnlyList<PlanFieldSpec> Fields);

/// <summary>A step added to the draft, shown as a tabular line.</summary>
public sealed record DraftStepLine(int Number, string Operation, string Description, PlanDraftStep Step);

/// <summary>
/// "Novo plano" (ROADMAP.md, feature 2): builds a plan without the LLM, previews the validated and simulated
/// effect, and submits it to the engine. The operator never sees JSON (references/NEGATIVAS_WEB.md).
/// </summary>
public sealed partial class NewPlanViewModel : PageViewModel
{
    public const int MaxSteps = 50;

    private readonly UserSession _user;
    private readonly IPlanService _plans;
    private readonly IDialogService _dialogs;
    private readonly IQueryService? _queries;
    private PlanDraft? _previewedDraft;
    private Dictionary<PlanLookup, IReadOnlyList<string>> _lookups = [];
    private IReadOnlyList<LocationRow> _locations = [];

    /// <param name="queries">Source of the pick-lists (SKUs, codes, partners); without it every field is free text.</param>
    public NewPlanViewModel(UserSession user, IPlanService plans, IDialogService dialogs, IQueryService? queries = null)
    {
        _user = user;
        _plans = plans;
        _dialogs = dialogs;
        _queries = queries;
        Operations = AllOperations.Where(o => user.Has(o.Permission)).ToList();
        SelectedOperation = Operations.FirstOrDefault();
    }

    public override string Title => "Novo plano";

    /// <summary>Every operation of the catalog, with a form generated from its schema (<see cref="PlanForms"/>).</summary>
    public static IReadOnlyList<PlanOperationOption> AllOperations { get; } = PlanForms.Build(Core.Engine.OperationCatalog.V1);

    public IReadOnlyList<PlanOperationOption> Operations { get; }
    public bool CanPlan => Operations.Count > 0;

    public ObservableCollection<PlanFieldViewModel> Fields { get; } = [];
    public ObservableCollection<DraftStepLine> Steps { get; } = [];
    public ObservableCollection<PlanIssue> Issues { get; } = [];

    [ObservableProperty]
    public partial PlanOperationOption? SelectedOperation { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasFormError))]
    public partial string? FormError { get; private set; }

    public bool HasFormError => FormError is not null;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasPreview))]
    public partial SummaryViewModel? PreviewEffect { get; private set; }

    [ObservableProperty]
    public partial string? PreviewRiskText { get; private set; }

    [ObservableProperty]
    public partial bool PreviewIsCritical { get; private set; }

    public bool HasPreview => PreviewEffect is not null;
    public bool HasIssues => Issues.Count > 0;
    public bool HasSteps => Steps.Count > 0;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(PreviewCommand), nameof(SubmitCommand), nameof(AddStepCommand))]
    public partial bool IsBusy { get; private set; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SubmitCommand))]
    public partial bool IsPreviewValid { get; private set; }

    /// <summary>The submitted plan, shown in the same board used by the Nexus and Planos.</summary>
    [ObservableProperty]
    public partial PlanBoardViewModel? Result { get; private set; }

    partial void OnSelectedOperationChanged(PlanOperationOption? value)
    {
        foreach (var old in Fields)
            old.PropertyChanged -= OnFieldChanged;
        Fields.Clear();
        FormError = null;
        if (value is null)
            return;
        foreach (var spec in value.Fields)
        {
            var field = new PlanFieldViewModel(spec) { Suggestions = _lookups.GetValueOrDefault(spec.Lookup, []) };
            field.PropertyChanged += OnFieldChanged;
            Fields.Add(field);
        }
    }

    /// <summary>Loads the pick-lists once per visit; a failure only leaves the fields as free text.</summary>
    public override async Task OnActivatedAsync()
    {
        if (_queries is null) return;
        try
        {
            var products = await _queries.GetProductsAsync();
            _locations = await _queries.GetLocationsAsync();
            var zones = await _queries.GetZonesAsync();
            var partners = await _queries.GetPartnersAsync();
            var orders = await _queries.GetOrdersAsync();
            _lookups = new Dictionary<PlanLookup, IReadOnlyList<string>>
            {
                [PlanLookup.Sku] = products.Select(p => p.Sku).ToList(),
                [PlanLookup.Unit] = products.Select(p => p.Unit).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToList(),
                [PlanLookup.Location] = _locations.Select(l => l.Code).ToList(),
                [PlanLookup.Zone] = zones.Where(z => z.IsActive).Select(z => z.Code).ToList(),
                [PlanLookup.Supplier] = partners.Where(p => p.Kind == "Fornecedor" && p.IsActive).Select(p => p.Name).ToList(),
                [PlanLookup.Customer] = partners.Where(p => p.Kind == "Cliente" && p.IsActive).Select(p => p.Name).ToList(),
                [PlanLookup.InboundOrder] = orders.Where(o => o.Kind == "Entrada").Select(o => o.Number).ToList(),
                [PlanLookup.OutboundOrder] = orders.Where(o => o.Kind == "Saída").Select(o => o.Number).ToList(),
            };
            foreach (var field in Fields)
                field.Suggestions = _lookups.GetValueOrDefault(field.Lookup, []);
        }
        catch (Exception ex)
        {
            Trace.TraceError("Plan form lookups unavailable: {0}", ex.GetType().Name);
        }
    }

    /// <summary>
    /// Choosing a zone fills what the operator would otherwise look up by hand: the next free code for a new location,
    /// the next free aisle and the zone's usual capacity for a range. Only empty fields are filled.
    /// </summary>
    private void OnFieldChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(PlanFieldViewModel.Value) || sender is not PlanFieldViewModel { Key: "zone" } zoneField)
            return;
        var zone = zoneField.Value.Trim().ToUpperInvariant();
        if (zone.Length == 0 || SelectedOperation is not { } op)
            return;

        var codes = _locations.Select(l => l.Code).ToList();
        var inZone = _locations.Where(l => l.Code.StartsWith(zone + "-", StringComparison.Ordinal)).ToList();
        var typical = inZone.GroupBy(l => l.Capacity).OrderByDescending(g => g.Count()).Select(g => g.Key).FirstOrDefault();
        void Fill(string key, string value)
        {
            if (Fields.FirstOrDefault(f => f.Key == key) is { } field && field.Value.Length == 0)
                field.Value = value;
        }

        if (op.Op == "create_location")
            Fill("code", PlanForms.NextLocationCode(zone, codes));
        if (op.Op == "create_location_range")
        {
            Fill("aisle", PlanForms.NextFreeAisle(zone, codes).ToString(CultureInfo.InvariantCulture));
            Fill("level_from", "1");
            Fill("level_to", "5");
        }
        if (op.Op is "create_location" or "create_location_range" && typical > 0)
            Fill("capacity", typical.ToString(CultureInfo.InvariantCulture));
    }

    private bool CanAddStep() => !IsBusy && SelectedOperation is not null && Steps.Count < MaxSteps;

    [RelayCommand(CanExecute = nameof(CanAddStep))]
    private void AddStep()
    {
        if (SelectedOperation is not { } operation)
            return;

        var args = new Dictionary<string, object?>();
        var parts = new List<string>();
        foreach (var field in Fields)
        {
            if (field.IsFlag)
            {
                if (field.Checked)
                {
                    args[field.Key] = true;
                    parts.Add(field.Label.Replace(" (opcional)", ""));
                }
                continue;
            }

            var text = field.Value.Trim();
            if (field.IsLines && text.Length > 0)
            {
                if (PlanForms.ParseLines(text, out var linesError) is not { } items)
                {
                    FormError = linesError;
                    return;
                }
                args[field.Key] = items;
                parts.Add($"{field.Label.Replace(" (opcional)", "")}: " +
                          string.Join(", ", items.Select(i => $"{i["sku"]} {i["qty"]}")));
                continue;
            }
            if (text.Length == 0)
            {
                if (field.IsRequired)
                {
                    FormError = $"Preencha {Lower(field.Label)}.";
                    return;
                }
                continue;
            }

            object? value = field.Kind switch
            {
                PlanFieldKind.Integer => int.TryParse(text, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var i) ? i : null,
                PlanFieldKind.Decimal => decimal.TryParse(text.Replace(',', '.'), NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var d) ? d : null,
                PlanFieldKind.Date => DateOnly.TryParseExact(text, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out _) ? text : null,
                _ => PlanForms.CodeFields.Contains(field.Key) ? text.ToUpperInvariant() : text,
            };
            if (value is null)
            {
                FormError = field.Kind == PlanFieldKind.Date
                    ? $"{field.Label.Replace(" (opcional)", "")}: use o formato AAAA-MM-DD."
                    : $"{field.Label.Replace(" (opcional)", "")}: informe um número.";
                return;
            }
            args[field.Key] = value;
            parts.Add($"{field.Label.Replace(" (opcional)", "")} {value}");
        }

        FormError = null;
        Steps.Add(new DraftStepLine(Steps.Count + 1, operation.Label, string.Join(" · ", parts), new PlanDraftStep(operation.Op, args)));
        foreach (var field in Fields)
        {
            field.Value = "";
            field.Checked = false;
        }
        OnStepsChanged();
    }

    [RelayCommand]
    private void RemoveStep(DraftStepLine line)
    {
        Steps.Remove(line);
        for (var i = 0; i < Steps.Count; i++)
            Steps[i] = Steps[i] with { Number = i + 1 };
        OnStepsChanged();
    }

    private bool CanPreview() => !IsBusy && Steps.Count > 0;

    [RelayCommand(CanExecute = nameof(CanPreview))]
    private async Task PreviewAsync()
    {
        var draft = BuildDraft();
        IsBusy = true;
        try
        {
            var preview = await _plans.PreviewAsync(draft);
            Issues.Clear();
            foreach (var issue in preview.Issues)
                Issues.Add(issue);
            OnPropertyChanged(nameof(HasIssues));

            if (preview.IsValid)
            {
                PreviewEffect = preview.Effect is { } effect ? new SummaryViewModel(effect) : null;
                PreviewIsCritical = preview.Risk == PlanRisk.Critical;
                PreviewRiskText = PreviewIsCritical
                    ? "Crítico: depois de enviado, o plano espera a aprovação de outro supervisor."
                    : "Reversível: ao enviar, o plano é executado na hora.";
                _previewedDraft = draft;
                IsPreviewValid = true;
            }
            else
            {
                ClearPreview();
            }
        }
        catch (Exception ex)
        {
            Trace.TraceError("Plan preview failed: {0}", ex.GetType().Name);
            ClearPreview();
            FormError = "Não foi possível pré-visualizar o plano. Tente de novo.";
        }
        finally
        {
            IsBusy = false;
        }
    }

    private bool CanSubmit() => !IsBusy && IsPreviewValid && _previewedDraft is not null;

    [RelayCommand(CanExecute = nameof(CanSubmit))]
    private async Task SubmitAsync()
    {
        if (_previewedDraft is not { } draft)
            return;
        IsBusy = true;
        try
        {
            var plan = await _plans.SubmitAsync(draft);
            Result?.Dispose();
            Result = new PlanBoardViewModel(plan, _user, _plans, _dialogs);
            Steps.Clear();
            OnStepsChanged();
        }
        catch (Exception ex)
        {
            Trace.TraceError("Plan submission failed: {0}", ex.GetType().Name);
            FormError = "Não foi possível enviar o plano. Nada foi gravado. Tente de novo.";
        }
        finally
        {
            IsBusy = false;
        }
    }

    private PlanDraft BuildDraft()
    {
        var first = AllOperations.First(o => o.Op == Steps[0].Step.Op);
        var mixed = Steps.Select(s => s.Step.Op).Distinct().Count() > 1;
        // A mixed plan keeps the skill of its first step so boards and audit still show a business name.
        return PlanDraft.Create(mixed ? "mixed" : first.Intent, first.Skill, Steps.Select(s => s.Step).ToList());
    }

    /// <summary>Any change to the steps invalidates the last preview: only what was previewed can be sent.</summary>
    private void OnStepsChanged()
    {
        ClearPreview();
        Issues.Clear();
        OnPropertyChanged(nameof(HasIssues));
        OnPropertyChanged(nameof(HasSteps));
        PreviewCommand.NotifyCanExecuteChanged();
        AddStepCommand.NotifyCanExecuteChanged();
    }

    private void ClearPreview()
    {
        PreviewEffect = null;
        PreviewRiskText = null;
        PreviewIsCritical = false;
        IsPreviewValid = false;
        _previewedDraft = null;
    }

    /// <summary>"Quantidade" → "quantidade", but acronyms such as "SKU" stay as they are.</summary>
    private static string Lower(string label)
    {
        var l = label.Replace(" (opcional)", "");
        return l.Length > 1 && char.IsUpper(l[1]) ? l : l.Length > 0 ? char.ToLowerInvariant(l[0]) + l[1..] : l;
    }
}
