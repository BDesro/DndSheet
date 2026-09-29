using System.ComponentModel;
using System.Windows;
using System.Windows.Input;
using Hearthsheet.App.ViewModels;

namespace Hearthsheet.App;

public partial class MainWindow
{
    public MainWindow() => InitializeComponent();

    private MainViewModel? Vm => DataContext as MainViewModel;

    private void OnClosing(object? sender, CancelEventArgs e)
    {
        if (Vm is { } vm && !vm.OnClosing()) e.Cancel = true;
    }

    private void OnExit(object sender, RoutedEventArgs e) => Close();

    private void CanPrint(object sender, CanExecuteRoutedEventArgs e) => e.CanExecute = Vm?.HasCharacter == true;

    private void OnPrint(object sender, ExecutedRoutedEventArgs e)
    {
        if (Vm is null) return;
        // The sheet must be laid out to print, so switch to it first.
        Vm.SelectedView = 0;
        Dispatcher.BeginInvoke(Sheet.Print, System.Windows.Threading.DispatcherPriority.Loaded);
    }
}
