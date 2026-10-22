# Workplace checks

Run from the repository root:

```text
dotnet run --project tests/WorkplaceChecks/WorkplaceChecks.csproj
```

The suite links the production core services directly and uses a unique temporary directory. It never reads application settings, a browser profile or a real GLPI portal. It checks source identity separation, bounded atomic personal storage, offline snapshots, filters using the production timeline column order, observable section changes, declared technician availability, explained dispatch estimates and deterministic ticket/solution relationships.

Real WPF event/binding checks are in `tests/WindowsStartupChecks/WorkplaceChecks.cs`. They execute only on Windows. Cross-compilation alone does not establish that the Windows interface or native notifications ran successfully.
