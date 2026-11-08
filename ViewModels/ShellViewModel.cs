using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using erp_inteligente.Core.Audit;
using erp_inteligente.Models;
using erp_inteligente.Services;

namespace erp_inteligente.ViewModels;

/// <summary>Main window: the menu column and the content container (references/UI.md, sections 2 and 3).</summary>
public sealed partial class ShellViewModel : ViewModelBase
{
    private readonly UserSession _user;
    private readonly IPlanService _plans;
    private readonly NavigationItem? _plansItem;

    /// <param name="auditStatus">Result of the hash-chain verification; a break is shown to users with <c>audit.read</c>.</param>
    /// <param name="databaseDescription">Where the encrypted database lives, shown in Configurações.</param>
    public ShellViewModel(ISessionService session, IQueryService queries, IPlanService plans, INexusService nexus,
        IDialogService dialogs, TimeProvider time, AuditVerification auditStatus, string databaseDescription)
    {
        _user = session.Current;
        _plans = plans;
        AuditAlert = !auditStatus.IsValid && _user.Has(Permissions.AuditRead)
            ? $"Cadeia de auditoria quebrada na linha {auditStatus.FirstBrokenId}. Avise o administrador."
            : null;

        NavigationItem[] menu =
        [
            new("Nexus", "Nexus", () => new NexusViewModel(_user, nexus, plans, dialogs, time), Permissions.PlanSubmit),
            new("Novo plano", "NewPlan", () => new NewPlanViewModel(_user, plans, dialogs), Permissions.PlanSubmit),
            new("Estoque", "Stock", () => new StockViewModel(queries, time), Permissions.StockRead),
            new("Endereços", "Locations", () => new LocationsViewModel(queries), Permissions.StockRead),
            new("Pedidos", "Orders", () => new OrdersViewModel(queries), Permissions.StockRead),
            new("Planos", "Plans", () => new PlansViewModel(_user, plans, dialogs), Permissions.PlanSubmit, Permissions.PlanApprove, Permissions.AuditRead),
            new("Receitas", "Recipes", () => new RecipesViewModel(queries), Permissions.StockRead),
            new("Auditoria", "Audit", () => new AuditViewModel(queries), Permissions.AuditRead),
        ];
        MenuItems = new ObservableCollection<NavigationItem>(menu.Where(IsAllowed));
        FooterItems = new ObservableCollection<NavigationItem>(
            new NavigationItem[] { new("Configurações", "Settings", () => new SettingsViewModel(_user, databaseDescription)) }.Where(IsAllowed));

        _plansItem = MenuItems.FirstOrDefault(i => i.IconKey == "Plans");
        _plans.PlanChanged += (_, _) => UpdatePendingBadge();
        _ = RefreshPendingBadgeAsync();

        var first = MenuItems.FirstOrDefault() ?? FooterItems.FirstOrDefault();
        if (first is not null)
            _ = NavigateAsync(first);
    }

    public ObservableCollection<NavigationItem> MenuItems { get; }
    public ObservableCollection<NavigationItem> FooterItems { get; }

    public string UserName => _user.DisplayName;
    public string UserDetail => $"{Capitalize(_user.RoleName)} · {_user.WarehouseCode}";

    /// <summary>Shown above the content when the audit hash chain is broken; null otherwise.</summary>
    public string? AuditAlert { get; }

    public bool HasAuditAlert => AuditAlert is not null;

    [ObservableProperty]
    public partial PageViewModel? CurrentPage { get; private set; }

    /// <summary>Raised when the user leaves; the app controller ends the session and shows the login.</summary>
    public event EventHandler? SignOutRequested;

    [RelayCommand]
    private Task NavigateAsync(NavigationItem item)
    {
        if (!MenuItems.Contains(item) && !FooterItems.Contains(item))
            return Task.CompletedTask;

        foreach (var other in MenuItems.Concat(FooterItems))
            other.IsActive = ReferenceEquals(other, item);

        CurrentPage = item.Page;
        return item.Page.OnActivatedAsync();
    }

    [RelayCommand]
    private void SignOut() => SignOutRequested?.Invoke(this, EventArgs.Empty);

    private async Task RefreshPendingBadgeAsync()
    {
        try
        {
            await _plans.RefreshAsync();
            UpdatePendingBadge();
        }
        catch (Exception ex)
        {
            // The badge is informative only; the Planos screen reports load errors itself.
            System.Diagnostics.Trace.TraceWarning("Pending badge refresh failed: {0}", ex.GetType().Name);
        }
    }

    private static string Capitalize(string text) =>
        text.Length == 0 ? text : char.ToUpperInvariant(text[0]) + text[1..];

    private bool IsAllowed(NavigationItem item) => item.RequiredAnyOf.Count == 0 || item.RequiredAnyOf.Any(_user.Has);

    /// <summary>Only users who can approve see the pending counter.</summary>
    private void UpdatePendingBadge()
    {
        if (_plansItem is not null)
            _plansItem.Badge = _plans.Plans.Count(p => ApprovalPolicy.CanDecide(p, _user));
    }
}
