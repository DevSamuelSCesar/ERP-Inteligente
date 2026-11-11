using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using erp_inteligente.Core.Memory;
using erp_inteligente.Models;
using erp_inteligente.Services;

namespace erp_inteligente.ViewModels;

/// <summary>A labelled choice of a combo box (kind or scope of a memory).</summary>
public sealed record MemoryChoice<T>(T Value, string Label)
{
    public override string ToString() => Label;
}

/// <summary>A row of the memory list.</summary>
public sealed class MemoryRow(MemoryRecord record)
{
    public MemoryRecord Record { get; } = record;
    public long Id => Record.Id;
    public string KindText => MemoriesViewModel.KindText(Record.Kind);
    public string ScopeText => MemoriesViewModel.ScopeText(Record.Scope, Record.ScopeRef);
    public string Content => Record.Content;
    public string ConfidenceText => Record.Confidence.ToString("0.00", CultureInfo.GetCultureInfo("pt-BR"));
    public long UseCount => Record.UseCount;
    public string OriginText => MemoriesViewModel.OriginText(Record.Origin);
    public string CreatedText => MemoriesViewModel.DateText(Record.CreatedAt);
}

/// <summary>
/// Memórias screen (references/UI.md, section 7.2): what the Nexus learned in this warehouse, for everybody with
/// <c>stock.read</c>; with <c>memory.manage</c> (the supervisor) also create, correct, archive and reactivate. The text
/// goes through the same protection filter the store applies (<see cref="MemoryFilter"/>), shown while typing.
/// </summary>
public sealed partial class MemoriesViewModel : PageViewModel
{
    private readonly IMemoryService _memories;
    private IReadOnlyList<MemoryRecord> _all = [];

    [GeneratedRegex(@"^[A-Za-z0-9_\-]{1,60}\z")]
    private static partial Regex ScopeRefPattern();

    public MemoriesViewModel(UserSession user, IMemoryService memories)
    {
        _memories = memories;
        CanManage = user.Has(Permissions.MemoryManage);
        EditKind = Kinds[0];
        EditScope = Scopes[0];
    }

    public override string Title => "Memórias";

    public bool CanManage { get; }
    public bool IsReadOnly => !CanManage;

    public ObservableCollection<MemoryRow> Rows { get; } = [];
    public bool IsEmpty => Rows.Count == 0;

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
    [NotifyPropertyChangedFor(nameof(HasSelection), nameof(SelectedMeta), nameof(CanCorrectSelected), nameof(CanArchiveSelected), nameof(CanReactivateSelected))]
    [NotifyCanExecuteChangedFor(nameof(CorrectCommand), nameof(ArchiveCommand), nameof(ReactivateCommand))]
    public partial MemoryRow? SelectedRow { get; set; }

    public bool HasSelection => SelectedRow is not null;

    public string SelectedMeta
    {
        get
        {
            if (SelectedRow?.Record is not { } r) return "";
            var text = $"{KindText(r.Kind)} · {ScopeText(r.Scope, r.ScopeRef)} · confiança {SelectedRow.ConfidenceText} · usada {r.UseCount}×";
            text += $" · {OriginText(r.Origin)}";
            if (!string.IsNullOrEmpty(r.CreatedByName)) text += $" ({r.CreatedByName})";
            text += $" · criada em {DateText(r.CreatedAt)}";
            if (r.SourcePlanId is { } plan) text += $" · plano {plan}";
            return text;
        }
    }

    public bool CanCorrectSelected => CanManage && SelectedRow?.Record.Status == MemoryStatus.Active;
    public bool CanArchiveSelected => CanCorrectSelected;
    public bool CanReactivateSelected => CanManage && SelectedRow?.Record.Status == MemoryStatus.Archived;

    // ---- editor -------------------------------------------------------------------------------------------

    public static IReadOnlyList<MemoryChoice<MemoryKind>> Kinds { get; } =
        Enum.GetValues<MemoryKind>().Select(k => new MemoryChoice<MemoryKind>(k, KindText(k))).ToList();

    public static IReadOnlyList<MemoryChoice<MemoryScope>> Scopes { get; } =
        Enum.GetValues<MemoryScope>().Select(s => new MemoryChoice<MemoryScope>(s, ScopeName(s))).ToList();

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsViewing))]
    [NotifyCanExecuteChangedFor(nameof(NewMemoryCommand), nameof(CorrectCommand), nameof(ArchiveCommand), nameof(ReactivateCommand), nameof(SaveCommand))]
    public partial bool IsEditing { get; private set; }

    public bool IsViewing => !IsEditing;

    /// <summary>A correction changes only the text; kind and scope are chosen when the memory is created.</summary>
    [ObservableProperty] public partial bool IsNew { get; private set; }
    [ObservableProperty] public partial string EditorTitle { get; private set; } = "";

    [ObservableProperty] public partial MemoryChoice<MemoryKind> EditKind { get; set; }
    [ObservableProperty] public partial MemoryChoice<MemoryScope> EditScope { get; set; }
    [ObservableProperty] public partial string EditScopeRef { get; set; } = "";
    [ObservableProperty] public partial string EditContent { get; set; } = "";

    partial void OnEditScopeChanged(MemoryChoice<MemoryScope> value)
    {
        OnPropertyChanged(nameof(IsScopeRefEnabled));
        Revalidate();
    }

    /// <summary>A general memory has no scope reference.</summary>
    public bool IsScopeRefEnabled => EditScope.Value != MemoryScope.Global;
    partial void OnEditScopeRefChanged(string value) => Revalidate();
    partial void OnEditContentChanged(string value) => Revalidate();

    [ObservableProperty] public partial string? ScopeError { get; private set; }
    [ObservableProperty] public partial string? ContentError { get; private set; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SaveCommand))]
    public partial bool IsEditorValid { get; private set; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SaveCommand), nameof(ArchiveCommand), nameof(ReactivateCommand), nameof(MaintainCommand))]
    public partial bool IsWorking { get; private set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasFeedback), nameof(FeedbackIsSuccess))]
    public partial string Feedback { get; private set; } = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(FeedbackIsSuccess))]
    public partial bool FeedbackIsError { get; private set; }

    public bool HasFeedback => Feedback.Length > 0;
    public bool FeedbackIsSuccess => HasFeedback && !FeedbackIsError;

    private long? _editingId;

    public override async Task OnActivatedAsync()
    {
        if (!IsEditing)
            await ReloadAsync(SelectedRow?.Id);
    }

    private async Task ReloadAsync(long? select)
    {
        try
        {
            _all = await _memories.ListAsync();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Trace.TraceError("Memory list failed: {0}", ex.GetType().Name);
            SetFeedback("Não foi possível carregar as memórias.", isError: true);
            _all = [];
        }
        ApplyFilter(select);
    }

    private void ApplyFilter(long? select = null)
    {
        select ??= SelectedRow?.Id;
        var filter = FilterText.Trim();
        var wanted = ShowActive ? MemoryStatus.Active : MemoryStatus.Archived;
        var rows = _all
            .Where(m => m.Status == wanted)
            .Where(m => filter.Length == 0
                        || m.Content.Contains(filter, StringComparison.CurrentCultureIgnoreCase)
                        || (m.ScopeRef ?? "").Contains(filter, StringComparison.CurrentCultureIgnoreCase))
            .OrderByDescending(m => m.Confidence)
            .ThenByDescending(m => m.Id)
            .Select(m => new MemoryRow(m))
            .ToList();

        Rows.Clear();
        foreach (var row in rows)
            Rows.Add(row);
        SelectedRow = Rows.FirstOrDefault(r => r.Id == select) ?? Rows.FirstOrDefault();
        OnPropertyChanged(nameof(IsEmpty));
    }

    private bool CanStartEditing() => CanManage && !IsEditing;

    [RelayCommand(CanExecute = nameof(CanStartEditing))]
    private void NewMemory()
    {
        _editingId = null;
        IsNew = true;
        EditorTitle = "Nova memória";
        EditKind = Kinds[0];
        EditScope = Scopes[0];
        EditScopeRef = "";
        EditContent = "";
        OpenEditor();
    }

    private bool CanCorrect() => CanStartEditing() && CanCorrectSelected;

    [RelayCommand(CanExecute = nameof(CanCorrect))]
    private void Correct()
    {
        var r = SelectedRow!.Record;
        _editingId = r.Id;
        IsNew = false;
        EditorTitle = $"Corrigir memória {r.Id}";
        EditKind = Kinds.Single(k => k.Value == r.Kind);
        EditScope = Scopes.Single(s => s.Value == r.Scope);
        EditScopeRef = r.ScopeRef ?? "";
        EditContent = r.Content;
        OpenEditor();
    }

    private bool CanArchive() => CanStartEditing() && !IsWorking && CanArchiveSelected;

    [RelayCommand(CanExecute = nameof(CanArchive))]
    private Task ArchiveAsync() => RunAsync(SelectedRow!.Id, id => _memories.ArchiveAsync(id), "Arquivada. O Nexus não a usa mais.");

    private bool CanReactivate() => CanStartEditing() && !IsWorking && CanReactivateSelected;

    [RelayCommand(CanExecute = nameof(CanReactivate))]
    private Task ReactivateAsync() => RunAsync(SelectedRow!.Id, id => _memories.ReactivateAsync(id), "Reativada.");

    private async Task RunAsync(long id, Func<long, Task> action, string done)
    {
        IsWorking = true;
        try
        {
            await action(id);
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

    private bool CanMaintain() => CanStartEditing() && !IsWorking;

    /// <summary>Feature 9: archives duplicates, near-duplicates and stale memories now (it also runs once a day).</summary>
    [RelayCommand(CanExecute = nameof(CanMaintain))]
    private async Task MaintainAsync()
    {
        IsWorking = true;
        try
        {
            var report = await _memories.MaintainAsync();
            SetFeedback(report.Total == 0
                ? "Manutenção feita: nada a arquivar."
                : $"Manutenção feita: {report.Duplicates} repetida(s), {report.Merged} parecida(s) e {report.Stale} sem uso arquivada(s). Veja em Arquivadas.",
                isError: false);
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
        IsWorking = true;
        try
        {
            var result = _editingId is { } id
                ? await _memories.UpdateAsync(id, EditContent)
                : await _memories.CreateAsync(EditKind.Value, EditScope.Value, ScopeRefOrNull(), EditContent);
            if (!result.Saved)
            {
                ContentError = result.Reason;
                SetFeedback(result.Reason ?? "A memória foi recusada.", isError: true);
                return;
            }
            IsEditing = false;
            ShowActive = true;
            SetFeedback(result.Reinforced ? "Já existia uma memória igual: a confiança dela foi reforçada." : "Memória salva.", isError: false);
            await ReloadAsync(result.Id);
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

    private void OpenEditor()
    {
        SetFeedback("", isError: false);
        IsEditing = true;
        Revalidate();
    }

    private string? ScopeRefOrNull() => EditScope.Value == MemoryScope.Global ? null : EditScopeRef.Trim();

    private void Revalidate()
    {
        if (!IsEditing) return;
        ScopeError = null;
        if (IsNew && EditScope.Value != MemoryScope.Global && !ScopeRefPattern().IsMatch(EditScopeRef.Trim()))
            ScopeError = $"Informe {ScopeHint(EditScope.Value)} (letras, números, - ou _, até 60).";
        var check = MemoryFilter.Check(EditContent);
        ContentError = check.Accepted ? null : check.Reason;
        IsEditorValid = ScopeError is null && ContentError is null;
    }

    private void SetFeedback(string text, bool isError)
    {
        FeedbackIsError = isError && text.Length > 0;
        Feedback = text;
    }

    internal static string MessageFor(Exception ex) => ex switch
    {
        Core.Policy.PolicyDeniedException or UnauthorizedAccessException => "Seu papel não permite alterar memórias.",
        MemoryStoreException => ex.Message,
        _ => "Falha ao gravar a memória. Nada foi alterado.",
    };

    internal static string KindText(MemoryKind kind) => kind switch
    {
        MemoryKind.Correction => "Correção",
        MemoryKind.Preference => "Preferência",
        MemoryKind.Pattern => "Padrão",
        _ => "Fato",
    };

    internal static string ScopeName(MemoryScope scope) => scope switch
    {
        MemoryScope.Global => "Geral",
        MemoryScope.Skill => "Skill",
        MemoryScope.Sku => "SKU",
        MemoryScope.Location => "Endereço",
        MemoryScope.Zone => "Zona",
        MemoryScope.Supplier => "Fornecedor",
        _ => "Cliente",
    };

    private static string ScopeHint(MemoryScope scope) => scope switch
    {
        MemoryScope.Skill => "o nome da skill (ex.: armazenagem)",
        MemoryScope.Sku => "o SKU (ex.: ABC-123)",
        MemoryScope.Location => "o endereço (ex.: A-02-03)",
        MemoryScope.Zone => "a zona (ex.: C)",
        MemoryScope.Supplier => "o código do fornecedor",
        _ => "o código do cliente",
    };

    internal static string ScopeText(MemoryScope scope, string? scopeRef) =>
        scope == MemoryScope.Global || string.IsNullOrEmpty(scopeRef) ? ScopeName(scope) : $"{ScopeName(scope)} {scopeRef}";

    internal static string OriginText(MemoryOrigin origin) => origin switch
    {
        MemoryOrigin.Ai => "IA",
        MemoryOrigin.System => "Sistema",
        _ => "Supervisor",
    };

    internal static string DateText(string? at) =>
        at is not null && DateTimeOffset.TryParse(at, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var when)
            ? when.ToLocalTime().ToString("dd/MM/yyyy HH:mm", CultureInfo.InvariantCulture)
            : "";
}
