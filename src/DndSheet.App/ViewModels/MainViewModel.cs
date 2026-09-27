using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Windows.Input;
using System.Windows.Threading;
using DndSheet.App.Composition;
using DndSheet.App.Mvvm;
using DndSheet.Core.Application;
using DndSheet.Core.Domain;
using DndSheet.Core.Rules;
using DndSheet.Core.Serialization;
using DndSheet.Infrastructure;
using DndSheet.Infrastructure.Security;
using DndSheet.Infrastructure.Updates;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;

namespace DndSheet.App.ViewModels;

public sealed record MainServices(
    CharacterLibrary Library, RestService Rest, UpdateService Updates, AppSettings Settings, AppPaths Paths,
    ISecretStore Secrets, Dialogs Dialogs, ILoggerFactory Loggers);

/// <summary>Shell: the character list, the open character, saving/autosave, import/export and updates.</summary>
public sealed class MainViewModel : Observable
{
    private readonly MainServices _s;
    private readonly ILogger _log;
    private readonly DispatcherTimer _autosave;

    public MainViewModel(MainServices services)
    {
        _s = services;
        _log = services.Loggers.CreateLogger<MainViewModel>();
        _autosave = new DispatcherTimer { Interval = TimeSpan.FromSeconds(Math.Max(0.5, services.Settings.Application.AutosaveDelaySeconds)) };
        _autosave.Tick += (_, _) => Save();
        Update = new UpdateViewModel(services.Updates, services.Paths, services.Dialogs, services.Settings.Updates, _log, BeforeUpdateShutdown);

        NewCharacter = new RelayCommand(CreateCharacter);
        DuplicateCharacter = new RelayCommand(Duplicate, () => Current is not null);
        DeleteCharacter = new RelayCommand(Delete, () => SelectedSummary is not null);
        ImportCharacter = new RelayCommand(Import);
        ExportCharacter = new RelayCommand(Export, () => Current is not null);
        SaveCharacter = new RelayCommand(() => Save(), () => Current is not null);
        ShowView = new RelayCommand(p => SelectedView = int.TryParse(p as string, out var v) ? v : 0, _ => Current is not null);
        ZoomIn = new RelayCommand(() => Zoom += 0.1);
        ZoomOut = new RelayCommand(() => Zoom -= 0.1);
        ZoomReset = new RelayCommand(() => Zoom = 1.0);
        OpenLogs = new RelayCommand(() => Process.Start(new ProcessStartInfo(_s.Paths.Logs) { UseShellExecute = true }));
        OpenDataFolder = new RelayCommand(() => Process.Start(new ProcessStartInfo(_s.Paths.Root) { UseShellExecute = true }));
        ManageToken = new RelayCommand(() => _s.Dialogs.ManageGitHubToken(_s.Secrets));
    }

    public ObservableCollection<CharacterSummary> Characters { get; } = [];
    public UpdateViewModel Update { get; }
    public string AppVersion => AppSettings.AppVersion;
    public bool IsDevelopmentMode => _s.Settings.Application.DevelopmentMode;

    public CharacterSummary? SelectedSummary
    {
        get;
        set
        {
            if (ReferenceEquals(field, value)) return;
            field = value;
            Raise();
            if (value is not null && value.Id != Current?.Character.Id) Open(value.Id);
        }
    }

    public CharacterViewModel? Current
    {
        get;
        private set
        {
            field?.Dispose();
            field = value;
            Raise();
            Raise(nameof(HasCharacter));
        }
    }

    public bool HasCharacter => Current is not null;

    /// <summary>0 = Sheet, 1 = Details, 2 = Play.</summary>
    public int SelectedView { get; set => Set(ref field, Math.Clamp(value, 0, 2)); }

    public double Zoom { get; set => Set(ref field, Math.Round(Math.Clamp(value, 0.5, 2.5), 2)); } = 1.0;

    public string SaveStatus { get; private set => Set(ref field, value); } = "";

    public ICommand NewCharacter { get; }
    public ICommand DuplicateCharacter { get; }
    public ICommand DeleteCharacter { get; }
    public ICommand ImportCharacter { get; }
    public ICommand ExportCharacter { get; }
    public ICommand SaveCharacter { get; }
    public ICommand ShowView { get; }
    public ICommand ZoomIn { get; }
    public ICommand ZoomOut { get; }
    public ICommand ZoomReset { get; }
    public ICommand OpenLogs { get; }
    public ICommand OpenDataFolder { get; }
    public ICommand ManageToken { get; }

    public void OnStarted()
    {
        ReloadList();
        SelectedSummary = Characters.FirstOrDefault();
        if (_s.Settings.Updates.Enabled && _s.Settings.Updates.CheckOnStartup && _s.Settings.Updates.IsConfigured)
            _ = Update.CheckInBackgroundAsync();
    }

    /// <summary>Called when the window is closing. Returns false to cancel the close.</summary>
    public bool OnClosing()
    {
        if (Current is null || !Current.Session.IsDirty) return true;
        if (Save()) return true;
        return _s.Dialogs.Confirm("The character could not be saved. Close anyway and lose recent changes?", "Unsaved changes");
    }

    private void ReloadList(Guid? select = null)
    {
        try
        {
            var list = _s.Library.List();
            Characters.Clear();
            foreach (var summary in list) Characters.Add(summary);
            // Re-point the selection at the refreshed summary; the character itself is already open.
            if (select is { } id) SelectedSummary = Characters.FirstOrDefault(c => c.Id == id);
        }
        catch (SqliteException ex)
        {
            _log.LogError(ex, "Could not list characters");
            _s.Dialogs.Error("The character list could not be read. See the log for details.");
        }
    }

    private void Open(Guid id)
    {
        if (Current is not null && Current.Session.IsDirty && !Save()) return;
        try
        {
            var character = _s.Library.Load(id);
            if (character is null)
            {
                _s.Dialogs.Error("That character no longer exists.");
                ReloadList();
                return;
            }
            SetCurrent(character);
        }
        catch (CharacterFormatException ex)
        {
            _log.LogError(ex, "Character {Id} could not be loaded", id);
            _s.Dialogs.Error($"This character could not be opened:\n\n{ex.Message}\n\nA backup of the database is kept in:\n{_s.Paths.Backups}");
        }
    }

    private void SetCurrent(Character character)
    {
        var session = new CharacterSession(character);
        session.Changed += OnCharacterChanged;
        Current = new CharacterViewModel(session, _s.Rest, _s.Dialogs, _s.Loggers.CreateLogger("Gameplay"));
        SaveStatus = "All changes saved";
        _log.LogDebug("View: character {Name} opened", character.Identity.Name);
    }

    private void OnCharacterChanged(object? sender, EventArgs e)
    {
        SaveStatus = "Unsaved changes";
        _autosave.Stop();
        _autosave.Start();
    }

    /// <returns>True when there was nothing to save or the save succeeded.</returns>
    public bool Save()
    {
        _autosave.Stop();
        if (Current is null) return true;
        try
        {
            _s.Library.Save(Current.Character);
            Current.Session.MarkSaved();
            SaveStatus = $"Saved {DateTime.Now:HH:mm:ss}";
            RefreshSummary(Current.Character);
            return true;
        }
        catch (Exception ex) when (ex is SqliteException or IOException or UnauthorizedAccessException)
        {
            _log.LogError(ex, "Save failed");
            SaveStatus = "Save failed — changes are still in memory";
            _s.Dialogs.Error($"The character could not be saved:\n\n{ex.Message}");
            return false;
        }
    }

    /// <summary>Keeps the sidebar entry (name, class, level) in step with edits without reloading everything.</summary>
    private void RefreshSummary(Character c)
    {
        var index = Characters.ToList().FindIndex(s => s.Id == c.Id);
        var fresh = _s.Library.List().FirstOrDefault(s => s.Id == c.Id);
        if (index < 0 || fresh is null || fresh == Characters[index]) return;
        var wasSelected = SelectedSummary?.Id == c.Id;
        Characters[index] = fresh;
        // Same character is already open; the SelectedSummary setter skips reopening when ids match.
        if (wasSelected) SelectedSummary = fresh;
    }

    private void CreateCharacter()
    {
        var options = _s.Dialogs.NewCharacter();
        if (options is null) return;
        if (!Save()) return;
        var character = _s.Library.Create(options);
        SetCurrent(character);
        ReloadList(character.Id);
        SelectedView = 1;
    }

    private void Duplicate()
    {
        if (Current is null || !Save()) return;
        var copy = _s.Library.Duplicate(Current.Character);
        SetCurrent(copy);
        ReloadList(copy.Id);
    }

    private void Delete()
    {
        var target = SelectedSummary;
        if (target is null) return;
        if (!_s.Dialogs.Confirm($"Permanently delete “{target.Name}”?\n\nExport it first if you may want it back. This cannot be undone.", "Delete character"))
            return;
        if (Current?.Character.Id == target.Id)
        {
            _autosave.Stop();
            Current = null;
        }
        _s.Library.Delete(target.Id);
        SelectedSummary = null;
        ReloadList();
        SelectedSummary = Characters.FirstOrDefault();
        SaveStatus = "";
    }

    private void Import()
    {
        var path = _s.Dialogs.OpenCharacterFile();
        if (path is null) return;
        try
        {
            if (!Save()) return;
            var character = _s.Library.Import(path);
            SetCurrent(character);
            ReloadList(character.Id);
            Current?.Play.Record($"Imported from {Path.GetFileName(path)}.");
        }
        catch (CharacterFormatException ex)
        {
            _log.LogWarning("Import rejected: {Reason}", ex.Message);
            _s.Dialogs.Error($"This file could not be imported:\n\n{ex.Message}");
        }
        catch (IOException ex)
        {
            _log.LogWarning(ex, "Import failed");
            _s.Dialogs.Error($"The file could not be read:\n\n{ex.Message}");
        }
    }

    private void Export()
    {
        if (Current is null) return;
        var path = _s.Dialogs.SaveCharacterFile(Current.Title);
        if (path is null) return;
        try
        {
            _s.Library.Export(Current.Character, path);
            Current.Play.Record($"Exported to {Path.GetFileName(path)}.");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _log.LogError(ex, "Export failed");
            _s.Dialogs.Error($"The character could not be exported:\n\n{ex.Message}");
        }
    }

    /// <summary>Before the updater takes over: persist everything. Returns false to abort the update.</summary>
    private bool BeforeUpdateShutdown() => Save();
}
