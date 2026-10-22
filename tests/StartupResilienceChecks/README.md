# Startup resilience checks

```text
dotnet run --project tests/StartupResilienceChecks/StartupResilienceChecks.csproj
```

The suite exercises bounded, verified preference archives before a version transition. It checks byte preservation, allowlisted files, reuse after an interrupted launch, rejection of an altered backup, version markers, size/budget limits and actionable startup advice. All files are synthetic and created under one temporary directory.

The actual Windows instance activation is checked by the `PackStartup` stage of `build-tools/Invoke-WindowsCi.ps1`. It requires an interactive Windows desktop and drives the real WinForms/WebView2 pack in an explicitly guarded temporary profile. It does not replace existing application data or create an application-data junction. See the [native harness protocol](../WindowsStartupChecks/README.md).
