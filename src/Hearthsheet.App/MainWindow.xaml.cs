using System.ComponentModel;
using System.Windows;
using System.Windows.Input;
using Hearthsheet.App.ViewModels;

namespace Hearthsheet.App;

public partial class MainWindow
{
    public MainWindow() => InitializeComponent();

    private MainViewModel? Vm => DataContext as MainViewModel;

    private bool _readyToClose;
    private bool _finalSyncing;

    private async void OnClosing(object? sender, CancelEventArgs e)
    {
        if (_readyToClose || Vm is not { } vm) return;
        if (_finalSyncing)
        {
            e.Cancel = true;
            return;
        }
        if (!vm.OnClosing())
        {
            e.Cancel = true;
            return;
        }
        if (!vm.Sync.IsSignedIn) return;

        // One last bounded sync pass, then close for real.
        e.Cancel = true;
        IsEnabled = false;
        _finalSyncing = true;
        try
        {
            await vm.Sync.FinalSyncAsync();
        }
        finally
        {
            _readyToClose = true;
            Close();
        }
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
