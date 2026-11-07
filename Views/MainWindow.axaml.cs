using System;
using Avalonia.Controls;
using erp_inteligente.ViewModels;

namespace erp_inteligente.Views;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (DataContext is ShellViewModel shell)
            shell.SignOutRequested += (_, _) => Close();
    }
}
