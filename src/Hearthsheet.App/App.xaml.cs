using System.Windows;
using System.Windows.Threading;
using Hearthsheet.App.Composition;
using Hearthsheet.App.ViewModels;
using Hearthsheet.Core.Application;
using Hearthsheet.Core.Rules;
using Hearthsheet.Core.Serialization;
using Hearthsheet.Infrastructure;
using Hearthsheet.Infrastructure.Logging;
using Hearthsheet.Infrastructure.Persistence;
using Hearthsheet.Infrastructure.Security;
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
            settings = AppSettings.Load(e.Args);
        }
        catch (Exception ex) when (ex is InvalidDataException or FormatException or InvalidOperationException)
        {
            MessageBox.Show($"The configuration file is invalid:\n\n{ex.Message}", "Hearthsheet", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(1);
            return;
        }

        var paths = new AppPaths(settings.Application.DataDirectory);
        var dev = settings.Application.DevelopmentMode;
        var sinks = new List<ILogSink> { new FileLogSink(paths.Logs, dev ? LogLevel.Debug : settings.Logging.FileMinimumLevel, settings.Logging.RetainDays) };
        if (dev && DevConsole.Open()) sinks.Add(new ConsoleLogSink());
        _loggerFactory = LoggerFactory.Create(b =>
        {
            b.ClearProviders();
            b.SetMinimumLevel(dev ? LogLevel.Debug : LogLevel.Information);
            b.AddProvider(new AppLoggerProvider(sinks));
        });
        _log = _loggerFactory.CreateLogger("App");
        InstallCrashHandlers();

        _log.LogInformation("Application started: version {Version}, build {Build}, development mode {Dev}",
            AppSettings.AppVersion, AppSettings.BuildConfiguration, dev);
        _log.LogDebug("Data directory: {Dir}", paths.Root);
        _log.LogDebug("Update source: {Owner}/{Repo} (enabled {Enabled})", settings.Updates.Owner, settings.Updates.Repository, settings.Updates.Enabled);

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
        var migrator = CharacterMigrator.Default;
        var repository = new SqliteCharacterRepository(paths.Database, migrator, factory.CreateLogger<SqliteCharacterRepository>());
        try
        {
            DatabaseBackups.RunDaily(repository, paths.Backups, keep: 10, factory.CreateLogger("Backup"));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or Microsoft.Data.Sqlite.SqliteException)
        {
            _log.LogWarning(ex, "Database backup failed; continuing");
        }

        var library = new CharacterLibrary(repository, new PortableCharacterFile(migrator), migrator,
            AppSettings.AppVersion, factory.CreateLogger<CharacterLibrary>());
        var secrets = new WindowsCredentialStore();
        var releaseSource = new GitHubReleaseSource(settings.Updates, secrets, userAgentVersion: AppSettings.AppVersion);
        var updates = new UpdateService(releaseSource, settings.Updates, SemVersion.Parse(AppSettings.AppVersion),
            paths.Updates, factory.CreateLogger<UpdateService>());

        return new MainViewModel(new MainServices(
            library, RestService.CreateDefault(), updates, settings, paths, secrets, new Dialogs(), factory));
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
