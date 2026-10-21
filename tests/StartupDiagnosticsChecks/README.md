# Startup diagnostics checks

Pure BCL console checks for the exact `StartupDiagnostics` and `AppPaths` sources used by the app. No GLPI portal, browser, WPF runtime, credentials or personal data are involved.

```sh
dotnet run --project tests/StartupDiagnosticsChecks/StartupDiagnosticsChecks.csproj -c Release
```

The checks cover startup stages and readiness, timestamped append persistence, environment metadata, exception types/HResult and bounded structural stacks, first-line/redacted messages, fallback on initial and subsequent write failures, both directories unavailable, a blocked latest-copy file, rapid unique restarts, concurrent steps, and the 64 KiB UTF-8 cap with reserved failure space. All fixtures use temporary directories and synthetic messages.

These checks do not establish that the Windows UI starts successfully. The WPF startup harness is separate.
