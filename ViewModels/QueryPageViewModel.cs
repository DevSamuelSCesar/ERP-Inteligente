using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using erp_inteligente.Models;

namespace erp_inteligente.ViewModels;

/// <summary>
/// Read-only query screen (references/UI.md, section 7): filter, totals strip and a DataGrid.
/// Changing data goes through the Nexus, never through these screens.
/// </summary>
public abstract partial class QueryPageViewModel<TRow> : PageViewModel
{
    private IReadOnlyList<TRow> _all = [];

    public ObservableCollection<TRow> Rows { get; } = [];
    public ObservableCollection<SummaryTotal> Totals { get; } = [];

    public abstract string FilterPlaceholder { get; }

    [ObservableProperty]
    public partial string FilterText { get; set; } = "";

    partial void OnFilterTextChanged(string value) => ApplyFilter();

    public override async Task OnActivatedAsync()
    {
        _all = await LoadAsync();
        ApplyFilter();
    }

    protected abstract Task<IReadOnlyList<TRow>> LoadAsync();

    protected abstract IEnumerable<string> SearchableText(TRow row);

    protected abstract IEnumerable<SummaryTotal> BuildTotals(IReadOnlyList<TRow> rows);

    /// <summary>Extra filters of a screen (besides the text); all rows by default.</summary>
    protected virtual bool Include(TRow row) => true;

    /// <summary>Applies the filters again after a screen-specific filter changed.</summary>
    protected void Refilter() => ApplyFilter();

    private void ApplyFilter()
    {
        var filter = FilterText.Trim();
        IReadOnlyList<TRow> rows = _all
            .Where(Include)
            .Where(r => filter.Length == 0 || SearchableText(r).Any(t => t.Contains(filter, StringComparison.CurrentCultureIgnoreCase)))
            .ToList();

        Rows.Clear();
        foreach (var row in rows)
            Rows.Add(row);

        Totals.Clear();
        foreach (var total in BuildTotals(rows))
            Totals.Add(total);
    }
}
