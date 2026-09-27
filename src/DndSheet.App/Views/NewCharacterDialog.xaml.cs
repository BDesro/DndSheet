using System.Windows;
using DndSheet.Core.Content;

namespace DndSheet.App.Views;

public partial class NewCharacterDialog
{
    public NewCharacterDialog() => InitializeComponent();

    public NewCharacterOptions? Result { get; private set; }

    private void OnCreate(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(NameBox.Text))
        {
            ShowError("Give the character a name.");
            NameBox.Focus();
            return;
        }
        if (!int.TryParse(LevelBox.Text, out var level) || level is < 1 or > 20)
        {
            ShowError("Level must be a whole number from 1 to 20.");
            LevelBox.Focus();
            return;
        }
        Result = new NewCharacterOptions(NameBox.Text, ClassBox.Text, level, SpeciesBox.Text, BackgroundBox.Text, PlayerBox.Text);
        DialogResult = true;
    }

    private void ShowError(string message)
    {
        ErrorText.Text = message;
        ErrorText.Visibility = Visibility.Visible;
    }
}
