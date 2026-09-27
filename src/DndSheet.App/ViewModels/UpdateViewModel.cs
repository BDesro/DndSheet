using System.Windows;
using System.Windows.Input;
using DndSheet.App.Mvvm;
using DndSheet.Core.Domain;
using DndSheet.Infrastructure;
using DndSheet.Infrastructure.Updates;
using Microsoft.Extensions.Logging;

namespace DndSheet.App.ViewModels;

public sealed class UpdateViewModel : Observable
{
    private readonly UpdateService _updates;
    private readonly AppPaths _paths;
    private readonly Dialogs _dialogs;
    private readonly UpdateOptions _options;
    private readonly ILogger _log;
    private readonly Func<bool> _beforeShutdown;

    public UpdateViewModel(UpdateService updates, AppPaths paths, Dialogs dialogs, UpdateOptions options, ILogger log, Func<bool> beforeShutdown)
    {
        _updates = updates;
        _paths = paths;
        _dialogs = dialogs;
        _options = options;
        _log = log;
        _beforeShutdown = beforeShutdown;
        ShowDialog = new RelayCommand(() =>
        {
            if (Available is null && !IsBusy) _ = CheckAsync();
            _dialogs.ShowUpdates(this);
        });
        Check = new RelayCommand(() => _ = CheckAsync(), () => !IsBusy);
        Install = new RelayCommand(() => _ = InstallAsync(), () => Available is not null && !IsBusy);
    }

    public string CurrentVersion => _updates.CurrentVersion.ToString();
    public string Source => _options.IsConfigured ? $"github.com/{_options.Owner}/{_options.Repository}" : "Not configured";

    public ReleaseInfo? Available { get; private set { Set(ref field, value); Raise(nameof(IsUpdateAvailable)); } }
    public bool IsUpdateAvailable => Available is not null;
    public bool IsBusy { get; private set => Set(ref field, value); }
    public double Progress { get; private set => Set(ref field, value); }
    public string Status { get; private set => Set(ref field, value); } = "";

    public ICommand ShowDialog { get; }
    public ICommand Check { get; }
    public ICommand Install { get; }

    /// <summary>Startup check: failures are logged, never shown (the user didn't ask).</summary>
    public async Task CheckInBackgroundAsync()
    {
        try
        {
            var result = await _updates.CheckAsync();
            if (result.IsUpdateAvailable) Available = result.Latest;
        }
        catch (UpdateException ex)
        {
            _log.LogWarning("Background update check failed: {Reason}", ex.Message);
        }
    }

    public async Task CheckAsync()
    {
        if (!_options.Enabled)
        {
            Status = "Updates are disabled in configuration.";
            return;
        }
        IsBusy = true;
        Status = "Checking for updates…";
        try
        {
            var result = await _updates.CheckAsync();
            Available = result.IsUpdateAvailable ? result.Latest : null;
            Status = result.IsUpdateAvailable
                ? $"Version {result.Latest!.Version} is available."
                : $"You have the latest version ({CurrentVersion}).";
        }
        catch (UpdateException ex)
        {
            _log.LogWarning("Update check failed: {Reason}", ex.Message);
            Status = ex.Message;
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task InstallAsync()
    {
        if (Available is not { } release) return;
        if (!_dialogs.Confirm($"Download and install version {release.Version}? The application will restart; your characters are saved first.", "Install update"))
            return;
        if (!_beforeShutdown())
        {
            Status = "Update cancelled: the current character could not be saved.";
            return;
        }

        IsBusy = true;
        Progress = 0;
        try
        {
            Status = $"Downloading {release.Version}…";
            var staged = await _updates.DownloadAndStageAsync(release, new Progress<double>(p => Progress = p));
            Status = "Installing — the application will restart.";
            _updates.LaunchInstaller(staged, AppContext.BaseDirectory, Environment.ProcessId, _paths.Logs);
            Application.Current.Shutdown();
        }
        catch (UpdateException ex)
        {
            _log.LogError("Update failed: {Reason}", ex.Message);
            Status = $"Update failed: {ex.Message} Nothing was changed.";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
        {
            _log.LogError(ex, "Update failed");
            Status = $"Update failed: {ex.Message} Nothing was changed.";
        }
        finally
        {
            IsBusy = false;
        }
    }
}
