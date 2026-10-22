# Startup resilience checks

```text
dotnet run --project tests/StartupResilienceChecks/StartupResilienceChecks.csproj
```

The suite exercises bounded, verified preference archives before a version transition. It checks byte preservation, allowlisted files, reuse after an interrupted launch, rejection of an altered backup, version markers, size/budget limits and actionable startup advice. All files are synthetic and created under one temporary directory.

The actual Windows instance activation is checked by the `PackStartup` stage of `build-tools/Invoke-WindowsCi.ps1`. That stage runs only in a disposable hosted Windows runner with an isolated application-data junction.
