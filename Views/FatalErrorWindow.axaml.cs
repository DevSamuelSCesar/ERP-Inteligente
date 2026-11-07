using Avalonia.Controls;
using Avalonia.Interactivity;

namespace erp_inteligente.Views;

public partial class FatalErrorWindow : Window
{
    public FatalErrorWindow() : this("")
    {
    }

    public FatalErrorWindow(string message)
    {
        InitializeComponent();
        MessageText.Text = message;
    }

    private void OnClose(object? sender, RoutedEventArgs e) => Close();
}
