using CommunityToolkit.Mvvm.ComponentModel;
using erp_inteligente.Services;

namespace erp_inteligente.ViewModels;

/// <summary>Reason requested when a plan is rejected; it becomes a memory for the Planner.</summary>
public sealed partial class RejectReasonDialogViewModel : ViewModelBase
{
    public int MaxLength => ApprovalPolicy.MaxReasonLength;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanConfirm), nameof(RemainingText))]
    public partial string Reason { get; set; } = "";

    public bool CanConfirm => Reason.Trim().Length is > 0 and <= ApprovalPolicy.MaxReasonLength;

    public string RemainingText => $"{MaxLength - Reason.Length} caracteres restantes";
}
