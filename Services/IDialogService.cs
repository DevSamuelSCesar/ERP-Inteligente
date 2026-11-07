using System.Threading.Tasks;

namespace erp_inteligente.Services;

/// <summary>Native dialogs requested by view models.</summary>
public interface IDialogService
{
    /// <returns>The rejection reason, or <c>null</c> when the user cancels.</returns>
    Task<string?> AskRejectionReasonAsync();
}
