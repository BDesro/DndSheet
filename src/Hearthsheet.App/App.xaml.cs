using System.Windows;
using System.Windows.Threading;
using Hearthsheet.App.Composition;
using Hearthsheet.App.ViewModels;
using Hearthsheet.Core.Application;
using Hearthsheet.Core.Rules;
using Hearthsheet.Core.Serialization;
using Hearthsheet.Core.Sync;
using Hearthsheet.Infrastructure;
using Hearthsheet.Infrastructure.Logging;
using Hearthsheet.Infrastructure.Persistence;
using Hearthsheet.Infrastructure.Security;
using Hearthsheet.Infrastructure.Sync;
using Hearthsheet.Infrastructure.Updates;
using Microsoft.Extensions.Logging;

namespace Hearthsheet.App;

/// <summary>Composition root: configuration, logging, crash handling and object wiring.</summary>
public partial class App
{
    private ILoggerFactory? _loggerFactory;
    private ILogger _log = Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        AppSettings settings;
        try
        {
            settings = AppSettings.Load();
        }
        catch (Exception ex) when (ex is InvalidDataException or FormatException or InvalidOperationException)
        {
            MessageBox.Show($"The configuration file is invalid:\n\n{ex.Message}", "Hearthsheet", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(1);
            return;
        }

        var paths = new AppPaths(settings.Application.DataDirectory);
        _loggerFactory = LoggerFactory.Create(b =>
        {
            b.ClearProviders();
            b.SetMinimumLevel(LogLevel.Trace);
            b.AddProvider(new FileLoggerProvider(paths.Logs, settings.Logging.FileMinimumLevel));
        });
        _log = _loggerFactory.CreateLogger("App");
        InstallCrashHandlers();

        _log.LogInformation("Application started: version {Version}, build {Build}",
            AppSettings.AppVersion, AppSettings.BuildConfiguration);
        _log.LogDebug("Data directory: {Dir}", paths.Root);
        _log.LogDebug("Update source: {Owner}/{Repo} (enabled {Enabled})", settings.Updates.Owner, settings.Updates.Repository, settings.Updates.Enabled);
        if (!string.IsNullOrWhiteSpace(settings.Sync.Url) && !settings.Sync.IsConfigured)
            _log.LogWarning("Cloud sync is disabled: Sync:Url must be an https URL and Sync:AnonKey must be set");

        MainViewModel main;
        try
        {
            main = Compose(settings, paths);
        }
        catch (Exception ex)
        {
            _log.LogCritical(ex, "Startup failed");
            MessageBox.Show($"Hearthsheet could not start:\n\n{ex.Message}\n\nDetails were written to:\n{paths.Logs}",
                "Hearthsheet", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(1);
            return;
        }

        var window = new MainWindow { DataContext = main };
        MainWindow = window;
        window.Show();
        main.OnStarted();
    }

    private MainViewModel Compose(AppSettings settings, AppPaths paths)
    {
        var factory = _loggerFactory!;
        var repository = new SqliteCharacterRepository(paths.Database, factory.CreateLogger<SqliteCharacterRepository>());
        try
        {
            DatabaseBackups.RunDaily(repository, paths.Backups, keep: 10, factory.CreateLogger("Backup"));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or Microsoft.Data.Sqlite.SqliteException)
        {
            _log.LogWarning(ex, "Database backup failed; continuing");
        }

        var library = new CharacterLibrary(repository, new PortableCharacterFile(),
            AppSettings.AppVersion, factory.CreateLogger<CharacterLibrary>());
        var secrets = new WindowsCredentialStore();
        var releaseSource = new GitHubReleaseSource(settings.Updates, userAgentVersion: AppSettings.AppVersion);
        var updates = new UpdateService(releaseSource, settings.Updates, Version.Parse(AppSettings.AppVersion),
            paths.Updates, factory.CreateLogger<UpdateService>());

        CloudServices? cloud = null;
        if (settings.Sync.IsConfigured)
        {
            var http = new SupabaseHttp(settings.Sync, userAgentVersion: AppSettings.AppVersion);
            var auth = new SupabaseAuth(http, secrets);
            var sync = new CloudSync(repository, repository, new SupabaseCharacterStore(http, auth), auth,
                factory.CreateLogger<CloudSync>());
            cloud = new CloudServices(auth, sync);
        }

        return new MainViewModel(new MainServices(
            repository, library, RestService.CreateDefault(), updates, settings, paths, new Dialogs(), factory, cloud));
    }

    private void InstallCrashHandlers()
    {
        DispatcherUnhandledException += OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
            _log.LogCritical(args.ExceptionObject as Exception, "Unhandled exception (terminating: {Terminating})", args.IsTerminating);
        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            _log.LogError(args.Exception, "Unobserved task exception");
            args.SetObserved();
        };
    }

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        _log.LogError(e.Exception, "Unhandled UI exception");
        // Keep running: the character is autosaved and the model is unaffected by a failed UI action.
        e.Handled = true;
        MessageBox.Show(
            $"Something went wrong:\n\n{e.Exception.Message}\n\nYour character data is safe. Details were written to the log.",
            "Hearthsheet", MessageBoxButton.OK, MessageBoxImage.Warning);
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _log.LogInformation("Application exiting (code {Code})", e.ApplicationExitCode);
        _loggerFactory?.Dispose();
        base.OnExit(e);
    }
}
