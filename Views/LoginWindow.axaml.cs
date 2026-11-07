using Avalonia.Controls;

namespace erp_inteligente.Views;

public partial class LoginWindow : Window
{
    public LoginWindow()
    {
        InitializeComponent();
        Opened += (_, _) => UsernameBox.Focus();
    }
}
