using System.Diagnostics;
using Hearthsheet.Updater;

// Replaces the application's install folder with a verified, staged update after the app exits,
// then restarts it. Runs from a copy outside the install folder so it can overwrite everything.
var options = UpdaterOptions.Parse(args);
if (options is null)
{
    Console.Error.WriteLine("Usage: Hearthsheet.Updater --pid <id> --source <dir> --target <dir> --exe <name> --log <dir>");
    return 2;
}

using var log = new UpdaterLog(options.LogDirectory);
try
{
    log.Info($"Waiting for process {options.ProcessId} to exit");
    WaitForExit(options.ProcessId, TimeSpan.FromSeconds(60));

    var installer = new UpdateInstaller(log);
    var result = installer.Install(options.SourceDirectory, options.TargetDirectory, options.Executable);
    log.Info(result.Succeeded ? "Update installed" : $"Update failed and was rolled back: {result.Error}");

    Process.Start(new ProcessStartInfo(Path.Combine(options.TargetDirectory, options.Executable))
    {
        UseShellExecute = false,
        WorkingDirectory = options.TargetDirectory,
    });
    return result.Succeeded ? 0 : 1;
}
catch (Exception ex)
{
    log.Error("Updater crashed: " + ex);
    return 1;
}

static void WaitForExit(int pid, TimeSpan timeout)
{
    try
    {
        using var process = Process.GetProcessById(pid);
        if (!process.WaitForExit(timeout)) throw new TimeoutException("The application did not exit; update aborted.");
    }
    catch (ArgumentException)
    {
        // Already exited.
    }
}
