using System.Threading.Tasks;
using Avalonia.Controls;
using erp_inteligente.Services;

namespace erp_inteligente.Views;

/// <summary>Opens native dialogs owned by the main window.</summary>
public sealed class DialogService(Window owner) : IDialogService
{
    public Task<string?> AskRejectionReasonAsync() => new RejectReasonDialog().ShowDialog<string?>(owner);
}
