using System.Windows;
using DndSheet.App.ViewModels;
using DndSheet.App.Views;
using DndSheet.Core.Content;
using DndSheet.Core.Domain;
using DndSheet.Core.Serialization;
using DndSheet.Infrastructure.Security;
using DndSheet.Infrastructure.Updates;
using Microsoft.Win32;

namespace DndSheet.App;

/// <summary>All modal UI launched from view models goes through here, keeping windows out of the view models.</summary>
public sealed class Dialogs
{
    private static Window? Owner => Application.Current?.MainWindow is { IsLoaded: true } w ? w : null;

    public bool Confirm(string message, string title = "Confirm") =>
        Show(message, title, MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes;

    public void Error(string message) =>
        Show(message, "DndSheet", MessageBoxButton.OK, MessageBoxImage.Error);

    public void Info(string message) =>
        Show(message, "DndSheet", MessageBoxButton.OK, MessageBoxImage.Information);

    private static MessageBoxResult Show(string message, string title, MessageBoxButton buttons, MessageBoxImage image) =>
        Owner is { } owner
            ? MessageBox.Show(owner, message, title, buttons, image, MessageBoxResult.None)
            : MessageBox.Show(message, title, buttons, image, MessageBoxResult.None);

    public NewCharacterOptions? NewCharacter()
    {
        var dialog = new NewCharacterDialog { Owner = Owner };
        return dialog.ShowDialog() == true ? dialog.Result : null;
    }

    public QuickAddResult? QuickAdd(string title, IReadOnlyList<object>? kinds, object? defaultKind, bool withDescription)
    {
        var dialog = new QuickAddDialog(title, kinds, defaultKind, withDescription) { Owner = Owner };
        return dialog.ShowDialog() == true ? dialog.Result : null;
    }

    /// <returns>True when the user chose to finish the short rest.</returns>
    public bool ShortRest(PlayViewModel play, Character character) =>
        new ShortRestDialog(play, character) { Owner = Owner }.ShowDialog() == true;

    public void ShowUpdates(UpdateViewModel vm) =>
        new UpdateDialog { Owner = Owner, DataContext = vm }.ShowDialog();

    public void ManageGitHubToken(ISecretStore store) =>
        new GitHubTokenDialog(store, GitHubReleaseSource.TokenKey) { Owner = Owner }.ShowDialog();

    public string? OpenCharacterFile()
    {
        var dialog = new OpenFileDialog
        {
            Title = "Import character",
            Filter = $"DndSheet character (*{PortableCharacterFile.FileExtension})|*{PortableCharacterFile.FileExtension}|All files (*.*)|*.*",
        };
        return dialog.ShowDialog(Owner) == true ? dialog.FileName : null;
    }

    public string? SaveCharacterFile(string suggestedName)
    {
        var safeName = string.Concat(suggestedName.Select(ch => Path.GetInvalidFileNameChars().Contains(ch) ? '_' : ch));
        var dialog = new SaveFileDialog
        {
            Title = "Export character",
            FileName = safeName + PortableCharacterFile.FileExtension,
            DefaultExt = PortableCharacterFile.FileExtension,
            Filter = $"DndSheet character (*{PortableCharacterFile.FileExtension})|*{PortableCharacterFile.FileExtension}",
        };
        return dialog.ShowDialog(Owner) == true ? dialog.FileName : null;
    }
}
