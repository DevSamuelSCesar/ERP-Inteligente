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
}

/// <summary>One input of the operation form; <see cref="Key"/> is the DSL argument name.</summary>
public sealed partial class PlanFieldViewModel(string key, string label, PlanFieldKind kind, bool required, string hint = "")
    : ObservableObject
{
    public string Key { get; } = key;
    public string Label { get; } = required ? label : $"{label} (opcional)";
    public PlanFieldKind Kind { get; } = kind;
    public bool IsRequired { get; } = required;
    /// <summary>Example shown while the field is empty; prefixed so it is never mistaken for a value.</summary>
    public string Hint { get; } = hint.Length == 0 ? "" : hint == "AAAA-MM-DD" ? hint : $"ex.: {hint}";
    public bool IsFlag => Kind == PlanFieldKind.Flag;
    public bool IsText => Kind != PlanFieldKind.Flag;

    [ObservableProperty]
    public partial string Value { get; set; } = "";

    [ObservableProperty]
    public partial bool Checked { get; set; }
}

/// <summary>An operation the user may plan, with its form fields (mirrors the DSL v1 schemas).</summary>
public sealed record PlanOperationOption(
    string Op, string Label, string Permission, string Intent, string Skill,
    IReadOnlyList<(string Key, string Label, PlanFieldKind Kind, bool Required, string Hint)> Fields);

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
    private PlanDraft? _previewedDraft;

    public NewPlanViewModel(UserSession user, IPlanService plans, IDialogService dialogs)
    {
        _user = user;
        _plans = plans;
        _dialogs = dialogs;
        Operations = AllOperations.Where(o => user.Has(o.Permission)).ToList();
        SelectedOperation = Operations.FirstOrDefault();
    }

    public override string Title => "Novo plano";

    public static IReadOnlyList<PlanOperationOption> AllOperations { get; } =
    [
        new("receive_goods", "Recebimento", "stock.receive", "receive", "recebimento",
        [
            ("sku", "SKU", PlanFieldKind.Text, true, "ABC-123"),
            ("qty", "Quantidade", PlanFieldKind.Integer, true, ""),
            ("location", "Doca", PlanFieldKind.Text, true, "DOCA-01"),
            ("lot", "Lote", PlanFieldKind.Text, false, ""),
            ("expires_on", "Validade", PlanFieldKind.Date, false, "AAAA-MM-DD"),
            ("inbound_order", "Pedido de entrada", PlanFieldKind.Text, false, "NF-FICT-000102"),
        ]),
        new("move_stock", "Movimentação", "stock.move", "putaway", "armazenagem",
        [
            ("sku", "SKU", PlanFieldKind.Text, true, "ABC-123"),
            ("qty", "Quantidade", PlanFieldKind.Integer, true, ""),
            ("from", "Origem", PlanFieldKind.Text, true, "DOCA-01"),
            ("to", "Destino", PlanFieldKind.Text, true, "A-02-01"),
            ("lot", "Lote", PlanFieldKind.Text, false, ""),
        ]),
        new("adjust_stock", "Ajuste de estoque", "stock.adjust", "adjust", "inventario",
        [
            ("sku", "SKU", PlanFieldKind.Text, true, "ALI-300"),
            ("qty", "Quantidade (negativa para baixa)", PlanFieldKind.Integer, true, "-1"),
            ("location", "Endereço", PlanFieldKind.Text, true, "C-01-01"),
            ("lot", "Lote", PlanFieldKind.Text, false, ""),
            ("reason", "Motivo", PlanFieldKind.Text, true, "Avaria na embalagem"),
        ]),
        new("create_product", "Cadastro de produto", "catalog.manage", "create_product", "cadastros",
        [
            ("sku", "SKU", PlanFieldKind.Text, true, "ABC-125"),
            ("name", "Nome", PlanFieldKind.Text, true, ""),
            ("unit", "Unidade", PlanFieldKind.Text, true, "cx"),
            ("description", "Descrição", PlanFieldKind.Text, false, ""),
            ("weight_kg", "Peso (kg)", PlanFieldKind.Decimal, false, ""),
            ("unit_cost", "Custo unitário", PlanFieldKind.Decimal, false, ""),
            ("tracks_expiry", "Controla validade", PlanFieldKind.Flag, false, ""),
        ]),
        new("create_location", "Cadastro de endereço", "location.manage", "create_location", "cadastros",
        [
            ("code", "Código", PlanFieldKind.Text, true, "A-03-01"),
            ("zone", "Zona", PlanFieldKind.Text, true, "A"),
            ("capacity", "Capacidade", PlanFieldKind.Integer, true, "60"),
        ]),
    ];

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
        Fields.Clear();
        FormError = null;
        if (value is null)
            return;
        foreach (var f in value.Fields)
            Fields.Add(new PlanFieldViewModel(f.Key, f.Label, f.Kind, f.Required, f.Hint));
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
                _ => field.Key is "sku" or "location" or "from" or "to" or "code" or "zone" ? text.ToUpperInvariant() : text,
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
