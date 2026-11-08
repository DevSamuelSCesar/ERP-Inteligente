using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using erp_inteligente.Core.Engine;
using erp_inteligente.Core.Skills;
using erp_inteligente.Models;
using erp_inteligente.Services;

namespace erp_inteligente.ViewModels;

/// <summary>One operation a skill declares, with its catalog label and risk.</summary>
public sealed record SkillOperationLine(string Name, string Label, string RiskText)
{
    public static SkillOperationLine Of(string op) =>
        OperationCatalog.TryGet(op, out var definition)
            ? new SkillOperationLine(op, definition.Label, definition.Risk == RiskLevel.Critical ? "Crítico" : "Reversível")
            : new SkillOperationLine(op, op, "");

    public static IReadOnlyList<SkillOperationLine> Of(IEnumerable<string> ops) => ops.Select(Of).ToList();
}

/// <summary>A row of the skill list.</summary>
public sealed class SkillRow(SkillRecord record)
{
    public SkillRecord Record { get; } = record;
    public string Name => Record.Name;
    public string Description => Record.Description;
    public string VersionText => "v" + Record.Version.ToString(CultureInfo.InvariantCulture);
    public string OriginText => SkillsViewModel.OriginText(Record);
}

/// <summary>A catalog operation in the editor, ticked when the skill declares it.</summary>
public sealed partial class SkillOperationChoice(SkillOperationLine line, Action changed) : ObservableObject
{
    public string Name => line.Name;
    public string Label => line.Label;
    public string RiskText => line.RiskText;

    [ObservableProperty]
    public partial bool IsSelected { get; set; }

    partial void OnIsSelectedChanged(bool value) => changed();
}

/// <summary>
/// Skills screen (references/UI.md, section 7.1): list and read-only detail for everybody; with <c>skill.manage</c>
/// (the supervisor) also create, edit, personalize a built-in, archive and reactivate. Validation is the same
/// <see cref="SkillDocument.Validate"/> the store and the Planner use, run on every change.
/// </summary>
public sealed partial class SkillsViewModel : PageViewModel
{
    private readonly ISkillService _skills;
    private IReadOnlyList<SkillRecord> _all = [];
    private SkillRecord? _editing;
    private bool _loadingEditor;

    public SkillsViewModel(UserSession user, ISkillService skills)
    {
        _skills = skills;
        CanManage = user.Has(Permissions.SkillManage);
        Choices = OperationCatalog.V1.Select(o => new SkillOperationChoice(SkillOperationLine.Of(o.Name), Revalidate)).ToList();
    }

    public override string Title => "Skills";

    /// <summary>Only the supervisor writes skills; everybody else gets the read-only screen.</summary>
    public bool CanManage { get; }
    public bool IsReadOnly => !CanManage;

    public ObservableCollection<SkillRow> Rows { get; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowArchived))]
    public partial bool ShowActive { get; set; } = true;

    public bool ShowArchived
    {
        get => !ShowActive;
        set => ShowActive = !value;
    }

    partial void OnShowActiveChanged(bool value) => ApplyFilter();

    [ObservableProperty]
    public partial string FilterText { get; set; } = "";

    partial void OnFilterTextChanged(string value) => ApplyFilter();

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSelection), nameof(SelectedOperations), nameof(HasSelectedOperations), nameof(SelectedMeta), nameof(SelectedOrigin),
        nameof(CanEditSelected), nameof(CanPersonalizeSelected), nameof(CanArchiveSelected), nameof(CanReactivateSelected))]
    [NotifyCanExecuteChangedFor(nameof(EditCommand), nameof(PersonalizeCommand), nameof(ArchiveCommand), nameof(ReactivateCommand))]
    public partial SkillRow? SelectedRow { get; set; }

    public bool HasSelection => SelectedRow is not null;
    public IReadOnlyList<SkillOperationLine> SelectedOperations => SkillOperationLine.Of(SelectedRow?.Record.Operations ?? []);
    public bool HasSelectedOperations => SelectedRow?.Record.Operations.Count > 0;

    // Which actions apply to the selected skill (the buttons that do not apply are hidden, not just disabled).
    public bool CanEditSelected => CanManage && SelectedRow?.Record is { IsBuiltIn: false, Status: SkillStatus.Active };
    public bool CanPersonalizeSelected => CanManage && SelectedRow?.Record.IsBuiltIn == true;
    public bool CanArchiveSelected => CanEditSelected;
    public bool CanReactivateSelected => CanManage && SelectedRow?.Record.Status == SkillStatus.Archived;

    public string SelectedOrigin => SelectedRow is null ? "" : OriginText(SelectedRow.Record);

    public string SelectedMeta
    {
        get
        {
            if (SelectedRow?.Record is not { } r) return "";
            var text = $"Versão {r.Version} · {OriginText(r)}";
            if (r.UpdatedAt is { } at && DateTimeOffset.TryParse(at, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var when))
                text += $" · atualizada em {when.ToLocalTime():dd/MM/yyyy HH:mm}";
            if (!string.IsNullOrEmpty(r.UpdatedByName))
                text += $" por {r.UpdatedByName}";
            return text;
        }
    }

    public bool IsEmpty => Rows.Count == 0;

    // ---- editor -------------------------------------------------------------------------------------------

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsViewing))]
    [NotifyCanExecuteChangedFor(nameof(NewSkillCommand), nameof(EditCommand), nameof(PersonalizeCommand), nameof(ArchiveCommand), nameof(ReactivateCommand))]
    public partial bool IsEditing { get; private set; }

    public bool IsViewing => !IsEditing;

    /// <summary>The name is chosen once, when the skill is created; editing keeps it.</summary>
    [ObservableProperty] public partial bool IsNameEditable { get; private set; }
    [ObservableProperty] public partial string EditorTitle { get; private set; } = "";

    [ObservableProperty] public partial string EditName { get; set; } = "";
    [ObservableProperty] public partial string EditDescription { get; set; } = "";
    [ObservableProperty] public partial string EditBody { get; set; } = "";

    partial void OnEditNameChanged(string value) => Revalidate();
    partial void OnEditDescriptionChanged(string value) => Revalidate();
    partial void OnEditBodyChanged(string value) => Revalidate();

    public IReadOnlyList<SkillOperationChoice> Choices { get; }

    [ObservableProperty] public partial string? NameError { get; private set; }
    [ObservableProperty] public partial string? DescriptionError { get; private set; }
    [ObservableProperty] public partial string? OperationsError { get; private set; }
    [ObservableProperty] public partial string? BodyError { get; private set; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SaveCommand))]
    public partial bool IsEditorValid { get; private set; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SaveCommand), nameof(ArchiveCommand), nameof(ReactivateCommand))]
    public partial bool IsWorking { get; private set; }

    /// <summary>Result of the last action: a line with icon and text, never a toast.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasFeedback))]
    public partial string Feedback { get; private set; } = "";

    [ObservableProperty] public partial bool FeedbackIsError { get; private set; }

    public bool HasFeedback => Feedback.Length > 0;
    public bool FeedbackIsSuccess => HasFeedback && !FeedbackIsError;

    partial void OnFeedbackIsErrorChanged(bool value) => OnPropertyChanged(nameof(FeedbackIsSuccess));

    public override async Task OnActivatedAsync()
    {
        if (!IsEditing)
            await ReloadAsync(SelectedRow?.Name);
    }

    private async Task ReloadAsync(string? select)
    {
        try
        {
            _all = await _skills.ListAsync();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Trace.TraceError("Skill list failed: {0}", ex.GetType().Name);
            SetFeedback("Não foi possível carregar as skills.", isError: true);
            _all = [];
        }
        ApplyFilter(select);
    }

    private void ApplyFilter(string? select = null)
    {
        select ??= SelectedRow?.Name;
        var filter = FilterText.Trim();
        var wanted = ShowActive ? SkillStatus.Active : SkillStatus.Archived;
        var rows = _all
            .Where(s => s.Status == wanted)
            .Where(s => filter.Length == 0
                        || s.Name.Contains(filter, StringComparison.CurrentCultureIgnoreCase)
                        || s.Description.Contains(filter, StringComparison.CurrentCultureIgnoreCase))
            .OrderBy(s => s.Name, StringComparer.Ordinal)
            .Select(s => new SkillRow(s))
            .ToList();

        Rows.Clear();
        foreach (var row in rows)
            Rows.Add(row);
        SelectedRow = Rows.FirstOrDefault(r => r.Name == select) ?? Rows.FirstOrDefault();
        OnPropertyChanged(nameof(IsEmpty));
    }

    private bool CanStartEditing() => CanManage && !IsEditing;

    [RelayCommand(CanExecute = nameof(CanStartEditing))]
    private void NewSkill()
    {
        _editing = null;
        OpenEditor("Nova skill", nameEditable: true, new SkillDocument("", "", [], "# \n\n## Passos\n1. \n\n## Regras\n- \n"));
    }

    private bool CanEdit() => CanStartEditing() && CanEditSelected;

    [RelayCommand(CanExecute = nameof(CanEdit))]
    private void Edit()
    {
        var record = SelectedRow!.Record;
        _editing = record;
        OpenEditor($"Editar {record.Name} (v{record.Version} → v{record.Version + 1})", nameEditable: false, Document(record));
    }

    private bool CanPersonalize() => CanStartEditing() && CanPersonalizeSelected;

    /// <summary>Copies a built-in into the editor; saving stores a version that replaces it, archiving brings it back.</summary>
    [RelayCommand(CanExecute = nameof(CanPersonalize))]
    private void Personalize()
    {
        var record = SelectedRow!.Record;
        _editing = null;
        OpenEditor($"Personalizar {record.Name}", nameEditable: false, Document(record));
    }

    private bool CanArchive() => CanStartEditing() && !IsWorking && CanArchiveSelected;

    [RelayCommand(CanExecute = nameof(CanArchive))]
    private Task ArchiveAsync() => RunAsync(SelectedRow!.Name, name => _skills.ArchiveAsync(name),
        SelectedRow.Record.OverridesBuiltIn ? "Arquivada. A versão embutida voltou a valer." : "Arquivada. O Nexus não a usa mais.");

    private bool CanReactivate() => CanStartEditing() && !IsWorking && CanReactivateSelected;

    [RelayCommand(CanExecute = nameof(CanReactivate))]
    private Task ReactivateAsync() => RunAsync(SelectedRow!.Name, name => _skills.ReactivateAsync(name), "Reativada.");

    private async Task RunAsync(string name, Func<string, Task> action, string done)
    {
        IsWorking = true;
        try
        {
            await action(name);
            SetFeedback(done, isError: false);
            await ReloadAsync(null);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            SetFeedback(MessageFor(ex), isError: true);
        }
        finally
        {
            IsWorking = false;
        }
    }

    private bool CanSave() => IsEditing && IsEditorValid && !IsWorking;

    [RelayCommand(CanExecute = nameof(CanSave))]
    private async Task SaveAsync()
    {
        var document = EditorDocument();
        IsWorking = true;
        try
        {
            var saved = await _skills.SaveAsync(document, SkillOrigin.User, _editing?.Version);
            IsEditing = false;
            ShowActive = true;
            SetFeedback($"Salva: {saved.Name} v{saved.Version}.", isError: false);
            await ReloadAsync(saved.Name);
        }
        catch (SkillValidationException ex)
        {
            ShowIssues(ex.Issues);
            SetFeedback("Corrija os campos indicados.", isError: true);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            SetFeedback(MessageFor(ex), isError: true);
        }
        finally
        {
            IsWorking = false;
        }
    }

    [RelayCommand]
    private void CancelEdit()
    {
        if (!IsEditing) return;
        IsEditing = false;
        SetFeedback("", isError: false);
    }

    private void OpenEditor(string title, bool nameEditable, SkillDocument document)
    {
        _loadingEditor = true;
        EditorTitle = title;
        IsNameEditable = nameEditable;
        EditName = document.Name;
        EditDescription = document.Description;
        EditBody = document.Body;
        foreach (var choice in Choices)
            choice.IsSelected = document.Operations.Contains(choice.Name, StringComparer.Ordinal);
        _loadingEditor = false;
        SetFeedback("", isError: false);
        IsEditing = true;
        Revalidate();
    }

    private SkillDocument EditorDocument() => new(EditName.Trim(), EditDescription.Trim(),
        Choices.Where(c => c.IsSelected).Select(c => c.Name).ToList(), EditBody);

    private void Revalidate()
    {
        if (_loadingEditor) return;
        var issues = EditorDocument().Normalized().Validate();
        if (IsNameEditable && issues.All(i => i.Field != "nome") && _all.Any(s => s.Name == EditName.Trim() && !s.IsBuiltIn))
            issues = [.. issues, new SkillIssue("nome", "Já existe uma skill com este nome. Escolha outro ou edite a existente.")];
        ShowIssues(issues);
    }

    private void ShowIssues(IReadOnlyList<SkillIssue> issues)
    {
        string? Of(string field) => issues.Where(i => i.Field == field).Select(i => i.Message) is var m && m.Any()
            ? string.Join(" ", m.Take(3)) : null;
        NameError = Of("nome");
        DescriptionError = Of("descricao");
        OperationsError = Of("operacoes");
        BodyError = Of("corpo");
        IsEditorValid = issues.Count == 0;
    }

    private void SetFeedback(string text, bool isError)
    {
        FeedbackIsError = isError && text.Length > 0;
        Feedback = text;
        OnPropertyChanged(nameof(FeedbackIsSuccess));
    }

    private static SkillDocument Document(SkillRecord r) => new(r.Name, r.Description, r.Operations, r.Body);

    internal static string OriginText(SkillRecord r) =>
        r.IsBuiltIn ? "Embutida"
        : r.OverridesBuiltIn ? "Personalizada"
        : r.Origin == SkillOrigin.Ai ? "Criada pela IA"
        : "Cadastrada";

    internal static string MessageFor(Exception ex) => ex switch
    {
        Core.Policy.PolicyDeniedException or UnauthorizedAccessException => "Seu papel não permite alterar skills.",
        SkillValidationException v => string.Join(" ", v.Issues.Take(3).Select(i => i.Message)),
        SkillConflictException or SkillStoreException => ex.Message,
        _ => "Falha ao gravar a skill. Nada foi alterado.",
    };
}
