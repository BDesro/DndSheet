using System.Windows;
using DndSheet.App.ViewModels;

namespace DndSheet.App.Views;

public partial class QuickAddDialog
{
    public QuickAddDialog(string title, IReadOnlyList<object>? kinds, object? defaultKind, bool withDescription)
    {
        InitializeComponent();
        Title = title;
        Heading.Text = title;
        if (kinds is null) KindField.Visibility = Visibility.Collapsed;
        else
        {
            KindBox.ItemsSource = kinds;
            KindBox.SelectedItem = defaultKind ?? kinds.FirstOrDefault();
        }
        if (!withDescription) DescriptionField.Visibility = Visibility.Collapsed;
    }

    public QuickAddResult? Result { get; private set; }

    private void OnAdd(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(NameBox.Text))
        {
            ErrorText.Visibility = Visibility.Visible;
            NameBox.Focus();
            return;
        }
        Result = new QuickAddResult(NameBox.Text.Trim(), KindBox.SelectedItem, DescriptionBox.Text);
        DialogResult = true;
    }
}
