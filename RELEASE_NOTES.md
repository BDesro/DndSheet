Maintenance release: a leaner Hearthsheet
- Behind the scenes cleanup: about 4,000 lines of unused code and documentation were removed, along with three packages the app no longer needs. Your characters and everything you see on the sheet work as before.

Removed
- Help > GitHub token is gone. Updates come from Hearthsheet's public releases, so no token is needed.
- Developer-only options are gone: development mode and its diagnostics console, the --dev flag, HEARTHSHEET_ environment variables, command-line settings, and the Sync:IntervalMinutes, Application:AutosaveDelaySeconds and Logging:RetainDays settings. Sync still runs every 5 minutes and autosave still waits 2 seconds.
- Update checks only offer regular releases. There is no beta channel anymore.

Good to know
- For more detail in the log file, set Logging:FileMinimumLevel to Debug in %LOCALAPPDATA%\Hearthsheet\appsettings.user.json.
