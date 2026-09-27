using System.Reflection;
using System.Runtime.InteropServices;
using DndSheet.Infrastructure;
using DndSheet.Infrastructure.Updates;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace DndSheet.App.Composition;

public sealed class ApplicationSettings
{
    /// <summary>Diagnostics only (console window, verbose logs). Never unlocks extra capabilities.</summary>
    public bool DevelopmentMode { get; set; }
    public string DataDirectory { get; set; } = "";
    public double AutosaveDelaySeconds { get; set; } = 2;
}

public sealed class LoggingSettings
{
    public LogLevel FileMinimumLevel { get; set; } = LogLevel.Information;
    public int RetainDays { get; set; } = 14;
}

public sealed record AppSettings(ApplicationSettings Application, LoggingSettings Logging, UpdateOptions Updates)
{
    /// <summary>
    /// Layered configuration, later sources win: shipped appsettings.json → appsettings.Development.json
    /// (Debug builds only) → per-user %LOCALAPPDATA%\DndSheet\appsettings.user.json → DNDSHEET_* environment
    /// variables → command line (--dev, or --Application:DevelopmentMode=true).
    /// </summary>
    public static AppSettings Load(string[] args)
    {
        var baseDir = AppContext.BaseDirectory;
        var switches = args.Contains("--dev", StringComparer.OrdinalIgnoreCase)
            ? new Dictionary<string, string?> { ["Application:DevelopmentMode"] = "true" }
            : [];

        var config = new ConfigurationBuilder()
            .AddJsonFile(Path.Combine(baseDir, "appsettings.json"), optional: true)
            .AddJsonFile(Path.Combine(baseDir, "appsettings.Development.json"), optional: true)
            .AddJsonFile(AppPaths.UserSettings, optional: true)
            .AddEnvironmentVariables("DNDSHEET_")
            .AddCommandLine(args.Where(a => !a.Equals("--dev", StringComparison.OrdinalIgnoreCase)).ToArray())
            .AddInMemoryCollection(switches)
            .Build();

        return new AppSettings(
            config.GetSection("Application").Get<ApplicationSettings>() ?? new(),
            config.GetSection("Logging").Get<LoggingSettings>() ?? new(),
            config.GetSection("Updates").Get<UpdateOptions>() ?? new());
    }

    public static string AppVersion { get; } =
        Assembly.GetEntryAssembly()?.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "0.0.0";

    public static string BuildConfiguration =>
#if DEBUG
        "Debug";
#else
        "Release";
#endif
}

/// <summary>Opens a console window next to the GUI for development-mode diagnostics.</summary>
public static class DevConsole
{
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool AllocConsole();

    public static bool Open()
    {
        if (!AllocConsole()) return false;
        // Rebind the .NET console streams to the newly allocated window.
        var stdout = new StreamWriter(Console.OpenStandardOutput()) { AutoFlush = true };
        Console.SetOut(stdout);
        Console.SetError(stdout);
        Console.Title = "DndSheet diagnostics";
        return true;
    }
}
