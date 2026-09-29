using System.ComponentModel;
using System.Windows;
using Hearthsheet.Infrastructure.Security;

namespace Hearthsheet.App.Views;

/// <summary>Write-only token management: the stored value is never read back into the UI.</summary>
public partial class GitHubTokenDialog
{
    private readonly ISecretStore _store;
    private readonly string _key;

    public GitHubTokenDialog(ISecretStore store, string key)
    {
        InitializeComponent();
        _store = store;
        _key = key;
        UpdateState();
    }

    private void UpdateState()
    {
        bool stored;
        try
        {
            stored = !string.IsNullOrEmpty(_store.Read(_key));
        }
        catch (Win32Exception)
        {
            stored = false;
        }
        StateText.Text = stored ? "A token is stored." : "No token is stored.";
        RemoveButton.IsEnabled = stored;
    }

    private void OnSave(object sender, RoutedEventArgs e)
    {
        var token = TokenBox.Password.Trim();
        if (token.Length < 20 || token.Any(char.IsWhiteSpace))
        {
            Feedback.Text = "That doesn't look like a GitHub token.";
            return;
        }
        try
        {
            _store.Write(_key, token);
            Feedback.Text = "Token saved to Windows Credential Manager.";
        }
        catch (Win32Exception ex)
        {
            Feedback.Text = "Could not store the token: " + ex.Message;
        }
        finally
        {
            TokenBox.Clear();
        }
        UpdateState();
    }

    private void OnRemove(object sender, RoutedEventArgs e)
    {
        try
        {
            _store.Delete(_key);
            Feedback.Text = "Stored token removed.";
        }
        catch (Win32Exception ex)
        {
            Feedback.Text = "Could not remove the token: " + ex.Message;
        }
        UpdateState();
    }
}
