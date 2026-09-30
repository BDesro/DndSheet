using System.Windows;
using Hearthsheet.Core.Sync;
using Hearthsheet.Infrastructure.Sync;

namespace Hearthsheet.App.Views;

/// <summary>Sign in, create an account, or reset a password with an emailed code. Passwords are never stored.</summary>
public partial class AccountDialog
{
    private enum Mode { SignIn, Create, RequestCode, Reset }

    private readonly SupabaseAuth _auth;
    private Mode _mode;

    public AccountDialog(SupabaseAuth auth)
    {
        InitializeComponent();
        _auth = auth;
        Show(Mode.SignIn);
        Loaded += (_, _) => EmailBox.Focus();
    }

    private void OnShowCreate(object sender, RoutedEventArgs e) => Show(Mode.Create);
    private void OnShowForgot(object sender, RoutedEventArgs e) => Show(Mode.RequestCode);
    private void OnShowSignIn(object sender, RoutedEventArgs e) => Show(Mode.SignIn);

    private void Show(Mode mode)
    {
        _mode = mode;
        (Heading.Text, PrimaryButton.Content) = mode switch
        {
            Mode.SignIn => ("Sign in", "Sign in"),
            Mode.Create => ("Create account", "Create account"),
            Mode.RequestCode => ("Reset password", "Send code"),
            _ => ("Reset password", "Reset password"),
        };
        CodeField.Visibility = Visible(mode == Mode.Reset);
        PasswordField.Visibility = Visible(mode != Mode.RequestCode);
        PasswordField.Header = mode == Mode.Reset ? "New password" : "Password";
        ConfirmField.Visibility = Visible(mode is Mode.Create or Mode.Reset);
        RememberBox.Visibility = Visible(mode != Mode.RequestCode);
        CreateLink.Visibility = Visible(mode == Mode.SignIn);
        ForgotLink.Visibility = Visible(mode == Mode.SignIn);
        BackLink.Visibility = Visible(mode != Mode.SignIn);
        Feedback.Text = "";
    }

    private static Visibility Visible(bool visible) => visible ? Visibility.Visible : Visibility.Collapsed;

    private async void OnPrimary(object sender, RoutedEventArgs e)
    {
        var email = EmailBox.Text.Trim();
        if (Validate(email) is { } problem)
        {
            Feedback.Text = problem;
            return;
        }
        IsEnabled = false;
        Feedback.Text = "Working…";
        try
        {
            var remember = RememberBox.IsChecked == true;
            switch (_mode)
            {
                case Mode.SignIn:
                    await _auth.SignInAsync(email, PasswordBox.Password, remember, CancellationToken.None);
                    break;
                case Mode.Create:
                    await _auth.SignUpAsync(email, PasswordBox.Password, remember, CancellationToken.None);
                    break;
                case Mode.RequestCode:
                    await _auth.RequestPasswordResetAsync(email, CancellationToken.None);
                    Show(Mode.Reset);
                    Feedback.Text = "If an account exists for that email, a code is on its way.";
                    return;
                case Mode.Reset:
                    await _auth.ResetPasswordAsync(email, CodeBox.Text.Trim(), PasswordBox.Password, remember, CancellationToken.None);
                    break;
            }
            DialogResult = true;
        }
        catch (CloudException ex)
        {
            Feedback.Text = ex.Message;
        }
        finally
        {
            PasswordBox.Clear();
            ConfirmBox.Clear();
            IsEnabled = true;
        }
    }

    private string? Validate(string email)
    {
        if (!email.Contains('@')) return "Enter your email address.";
        if (_mode == Mode.RequestCode) return null;
        if (_mode == Mode.Reset && CodeBox.Text.Trim().Length == 0) return "Enter the code from the email.";
        if (PasswordBox.Password.Length == 0) return "Enter your password.";
        if (_mode is Mode.Create or Mode.Reset)
        {
            if (PasswordBox.Password.Length < 8) return "Use at least 8 characters.";
            if (PasswordBox.Password != ConfirmBox.Password) return "The passwords don't match.";
        }
        return null;
    }
}
