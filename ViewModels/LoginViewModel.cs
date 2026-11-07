using System;
using System.Diagnostics;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using erp_inteligente.Core.Auth;
using erp_inteligente.Models;

namespace erp_inteligente.ViewModels;

/// <summary>
/// Login screen. Failures always show the same message, so the screen never reveals whether a user exists
/// (references/ARQUITETURA.md, section 12.5).
/// </summary>
public sealed partial class LoginViewModel(AuthService auth, TimeProvider time, string? notice = null) : ViewModelBase
{
    public const string InvalidCredentials = "Usuário ou senha inválidos.";

    /// <summary>Why the previous session ended (e.g. inactivity), shown above the form.</summary>
    public string? Notice { get; } = notice;

    public bool HasNotice => Notice is not null;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(LoginCommand))]
    public partial string Username { get; set; } = "";

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(LoginCommand))]
    public partial string Password { get; set; } = "";

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(LoginCommand))]
    public partial bool IsBusy { get; private set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasError))]
    public partial string? ErrorText { get; private set; }

    public bool HasError => ErrorText is not null;

    /// <summary>Raised on the UI thread after a successful login.</summary>
    public event EventHandler<UserSession>? LoggedIn;

    private bool CanLogin() => !IsBusy && Username.Trim().Length > 0 && Password.Length > 0;

    [RelayCommand(CanExecute = nameof(CanLogin))]
    private async Task LoginAsync()
    {
        IsBusy = true;
        ErrorText = null;
        try
        {
            var result = await auth.LoginAsync(Username, Password);
            switch (result)
            {
                case LoginResult.Succeeded success:
                    LoggedIn?.Invoke(this, success.Session);
                    break;
                case LoginResult.Locked locked:
                    var minutes = Math.Max(1, (int)Math.Ceiling((locked.Until - time.GetUtcNow()).TotalMinutes));
                    ErrorText = $"Acesso bloqueado por tentativas inválidas. Tente de novo em {minutes} min.";
                    break;
                default:
                    ErrorText = InvalidCredentials;
                    break;
            }
        }
        catch (Exception ex)
        {
            Trace.TraceError("Login failed unexpectedly: {0}", ex.GetType().Name);
            ErrorText = "Não foi possível entrar agora. Tente de novo.";
        }
        finally
        {
            Password = "";
            IsBusy = false;
        }
    }
}
