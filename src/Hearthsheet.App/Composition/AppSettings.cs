using System.Reflection;
using Hearthsheet.Infrastructure;
using Hearthsheet.Infrastructure.Sync;
using Hearthsheet.Infrastructure.Updates;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace Hearthsheet.App.Composition;

public sealed class ApplicationSettings
{
    public string DataDirectory { get; set; } = "";
}

public sealed class LoggingSettings
{
    public LogLevel FileMinimumLevel { get; set; } = LogLevel.Information;
}

public sealed record AppSettings(ApplicationSettings Application, LoggingSettings Logging, UpdateOptions Updates, SupabaseOptions Sync)
{
    /// <summary>
    /// Layered configuration, later sources win: shipped appsettings.json → per-user
    /// %LOCALAPPDATA%\Hearthsheet\appsettings.user.json.
    /// </summary>
    public static AppSettings Load()
    {
        var config = new ConfigurationBuilder()
            .AddJsonFile(Path.Combine(AppContext.BaseDirectory, "appsettings.json"), optional: true)
            .AddJsonFile(AppPaths.UserSettings, optional: true)
            .Build();

        return new AppSettings(
            config.GetSection("Application").Get<ApplicationSettings>() ?? new(),
            config.GetSection("Logging").Get<LoggingSettings>() ?? new(),
            config.GetSection("Updates").Get<UpdateOptions>() ?? new(),
            config.GetSection("Sync").Get<SupabaseOptions>() ?? new());
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
