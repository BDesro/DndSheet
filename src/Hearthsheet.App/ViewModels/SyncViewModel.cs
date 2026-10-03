using System.Windows.Input;
using System.Windows.Threading;
using Hearthsheet.App.Mvvm;
using Hearthsheet.Core.Domain;
using Hearthsheet.Core.Sync;
using Hearthsheet.Infrastructure.Sync;
using Microsoft.Extensions.Logging;

namespace Hearthsheet.App.ViewModels;

public sealed record CloudServices(SupabaseAuth Auth, CloudSync Sync);

/// <summary>
/// Account state, the status-bar line and when sync passes run: startup, sign-in, every few minutes, "Sync now"
/// and closing. Never after individual saves. Passes run off the UI thread; results are applied back on it.
/// </summary>
public sealed class SyncViewModel : Observable
{
    private static readonly TimeSpan FinalSyncLimit = TimeSpan.FromSeconds(5);

    private readonly CloudServices? _cloud;
    private readonly MainViewModel _main;
    private readonly Dialogs _dialogs;
    private readonly ILogger _log;
    private readonly DispatcherTimer _timer = new();

    public SyncViewModel(CloudServices? cloud, MainViewModel main, Dialogs dialogs, ILogger log)
    {
        _cloud = cloud;
        _main = main;
        _dialogs = dialogs;
        _log = log;
        _timer.Interval = TimeSpan.FromMinutes(5);
        _timer.Tick += (_, _) => _ = RunAsync();
        SignIn = new RelayCommand(() => _ = SignInAsync(), () => IsConfigured && !IsSignedIn);
        SignOut = new RelayCommand(() => _ = SignOutAsync(), () => IsSignedIn);
        SyncNow = new RelayCommand(() => _ = RunAsync(), () => IsSignedIn && !IsBusy);
        Status = cloud is null ? "" : "Not signed in";
    }

    public bool IsConfigured => _cloud is not null;
    public bool IsSignedIn => _cloud?.Auth.IsSignedIn == true;
    public string AccountLabel => IsSignedIn ? $"Signed in as {_cloud!.Auth.Email}" : "Not signed in";
    public string AccountEmail => IsSignedIn ? _cloud!.Auth.Email ?? "" : "";
    public bool IsBusy { get; private set => Set(ref field, value); }
    public string Status { get; private set => Set(ref field, value); } = "";

    public ICommand SignIn { get; }
    public ICommand SignOut { get; }
    public ICommand SyncNow { get; }

    public async Task StartAsync()
    {
        if (_cloud is null || !_cloud.Auth.Restore()) return;
        OnAccountChanged();
        await RunAsync();
    }

    /// <summary>After the window's final save: one pass, capped so closing never hangs.</summary>
    public Task FinalSyncAsync() => IsSignedIn ? RunAsync(final: true) : Task.CompletedTask;

    public async Task RunAsync(bool final = false)
    {
        if (_cloud is null || !IsSignedIn || (IsBusy && !final)) return;
        IsBusy = true;
        Status = "Syncing…";
        using var limit = new CancellationTokenSource();
        if (final) limit.CancelAfter(FinalSyncLimit);
        try
        {
            // The open character's unsaved edits go out with this pass; an unedited one must not be marked dirty.
            if (!final && _main.Current?.Session.IsDirty == true) _main.Save();
            var sync = _cloud.Sync;
            var result = await Task.Run(() => sync.RunAsync(() => _main.OpenCharacterId, waitIfBusy: final, limit.Token), limit.Token);
            await ApplyAsync(result);
        }
        catch (OperationCanceledException)
        {
            Status = "Sync timed out — will finish next time";
        }
        catch (Exception ex)
        {
            // A bug in a background pass must never take the app down; local data is unaffected.
            _log.LogError(ex, "Sync pass failed");
            Status = "Sync failed — see the log";
        }
        finally
        {
            IsBusy = false;
            CommandManager.InvalidateRequerySuggested();
        }
    }

    private async Task ApplyAsync(SyncResult result)
    {
        switch (result.Outcome)
        {
            case SyncOutcome.Synced:
                _main.ApplySyncResult(result);
                Status = $"Synced {DateTime.Now:HH:mm}" + Details(result);
                break;
            case SyncOutcome.Offline:
                Status = "Offline";
                break;
            case SyncOutcome.Unavailable:
                Status = "Cloud unavailable";
                break;
            case SyncOutcome.SignedOut:
                Status = "Signed out – sign in to resume sync";
                OnAccountChanged();
                break;
            case SyncOutcome.NotSignedIn:
                Status = "Not signed in";
                break;
            case SyncOutcome.AccountMismatch:
                Status = await ConfirmAccountLinkAsync() ? "Ready to sync" : "Not signed in";
                break;
            case SyncOutcome.Busy:
                break;
        }
    }

    private static string Details(SyncResult result) =>
        (result.ConflictCopies > 0 ? $" · {result.ConflictCopies} conflict {(result.ConflictCopies == 1 ? "copy" : "copies")} kept" : "")
        + (result.Warnings.Count > 0 ? $" · {result.Warnings.Count} not synced (see log)" : "");

    private async Task SignInAsync()
    {
        if (_cloud is null || !_dialogs.SignIn(_cloud.Auth)) return;
        try
        {
            if (!await ConfirmAccountLinkAsync()) return;
            _log.LogInformation("Signed in to cloud sync");
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Linking the cloud account failed");
            Status = "Sign-in failed — see the log";
            try { await _cloud.Auth.SignOutAsync(CancellationToken.None); }
            catch (Exception signOutEx) { _log.LogWarning(signOutEx, "Sign-out after a failed link also failed"); }
            OnAccountChanged();
            return;
        }
        OnAccountChanged();
        await RunAsync();
    }

    /// <summary>Signing in as a different account than this computer last synced with: ask what to do.</summary>
    private async Task<bool> ConfirmAccountLinkAsync()
    {
        var userId = _cloud!.Auth.UserId!;
        var linked = _cloud.Sync.LinkedAccount;
        if (linked is null || linked == userId) return true;

        var upload = _dialogs.AccountSwitch();
        if (upload is null || !_main.WithCharacterClosed(() => _cloud.Sync.Relink(userId, upload.Value)))
        {
            await SignOutAsync();
            return false;
        }
        _log.LogInformation("Linked this computer to a different cloud account (upload existing: {Upload})", upload.Value);
        return true;
    }

    private async Task SignOutAsync()
    {
        if (_cloud is null) return;
        await _cloud.Auth.SignOutAsync(CancellationToken.None);
        _log.LogInformation("Signed out of cloud sync");
        Status = "Not signed in";
        OnAccountChanged();
    }

    private void OnAccountChanged()
    {
        if (IsSignedIn) _timer.Start();
        else _timer.Stop();
        Raise(nameof(IsSignedIn));
        Raise(nameof(AccountLabel));
        Raise(nameof(AccountEmail));
        CommandManager.InvalidateRequerySuggested();
    }
}
