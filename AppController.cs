using System;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using erp_inteligente.Models;
using erp_inteligente.ViewModels;
using erp_inteligente.Views;

namespace erp_inteligente;

/// <summary>
/// Moves the app between the login window and the main window: starts the session after login, renews it on
/// keyboard and mouse input, ends it on sign-out or after the idle timeout, and returns to the login.
/// </summary>
/// <param name="expiryCheckInterval">How often the idle timeout is checked; tests use a short interval.</param>
internal sealed class AppController(IWindowHost desktop, AppHost host, TimeSpan? expiryCheckInterval = null)
{
    public const string ExpiredNotice = "Sessão encerrada após 15 minutos sem uso. Entre de novo.";

    private readonly TimeSpan _expiryCheckInterval = expiryCheckInterval ?? TimeSpan.FromSeconds(20);

    public void Start(string? notice = null) => ShowLogin(notice);

    private void ShowLogin(string? notice)
    {
        var viewModel = new LoginViewModel(host.Auth, host.Time, notice);
        var window = new LoginWindow { DataContext = viewModel };
        var handedOver = false;

        viewModel.LoggedIn += (_, session) =>
        {
            handedOver = true;
            ShowShell(session);
            window.Close();
        };
        // Closing the login window without signing in quits the app.
        window.Closed += (_, _) =>
        {
            if (!handedOver)
                desktop.Shutdown();
        };

        desktop.MainWindow = window;
        window.Show();
    }

    private void ShowShell(UserSession session)
    {
        host.Session.Start(session);
        host.VerifyAudit();

        var window = new MainWindow();
        var shell = host.CreateShell(new DialogService(window));
        window.DataContext = shell;

        var ending = false;
        var timer = new DispatcherTimer { Interval = _expiryCheckInterval };

        void End(string? notice)
        {
            if (ending)
                return;
            ending = true;
            timer.Stop();
            ShowLogin(notice);
            window.Close();
        }

        // Any keyboard or mouse input in the window counts as activity, even if a control handled it.
        void Touch(object? sender, RoutedEventArgs e) => host.Session.Touch();
        window.AddHandler(InputElement.KeyDownEvent, Touch, RoutingStrategies.Tunnel, handledEventsToo: true);
        window.AddHandler(InputElement.PointerPressedEvent, Touch, RoutingStrategies.Tunnel, handledEventsToo: true);
        window.AddHandler(InputElement.PointerWheelChangedEvent, Touch, RoutingStrategies.Tunnel, handledEventsToo: true);

        timer.Tick += (_, _) =>
        {
            if (host.Session.CheckExpiry())
                End(ExpiredNotice);
        };
        shell.SignOutRequested += (_, _) =>
        {
            host.Session.SignOut();
            End(null);
        };
        // Closing the main window signs out and quits.
        window.Closing += (_, _) =>
        {
            if (ending)
                return;
            ending = true;
            timer.Stop();
            if (host.Session.IsActive)
                host.Session.SignOut();
            desktop.Shutdown();
        };

        desktop.MainWindow = window;
        window.Show();
        timer.Start();
    }
}
