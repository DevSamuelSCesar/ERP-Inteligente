using Avalonia.Controls;
using Avalonia.Interactivity;
using erp_inteligente.ViewModels;

namespace erp_inteligente.Views;

/// <summary>Returns the trimmed reason, or <c>null</c> when cancelled.</summary>
public partial class RejectReasonDialog : Window
{
    public RejectReasonDialog()
    {
        InitializeComponent();
        DataContext = new RejectReasonDialogViewModel();
        Opened += (_, _) => ReasonBox.Focus();
    }

    private void OnConfirm(object? sender, RoutedEventArgs e)
    {
        if (DataContext is RejectReasonDialogViewModel { CanConfirm: true } viewModel)
            Close(viewModel.Reason.Trim());
    }

    private void OnCancel(object? sender, RoutedEventArgs e) => Close(null);
}
