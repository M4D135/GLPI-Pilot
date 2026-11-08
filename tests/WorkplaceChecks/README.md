# Workplace checks

Run from the repository root:

```text
dotnet run --project tests/WorkplaceChecks/WorkplaceChecks.csproj
```

The suite links the production core services directly and uses a unique temporary directory. It never reads application settings, a browser profile or a real GLPI portal. Its 142 checks cover source identity separation, bounded atomic personal storage, offline observations, filters and reading positions, row changes, explicitly observed tasks/validations/wait intervals/actor roles, previous-session briefings, recurring calendars and half-day capacities, category duration models and uncertainty, explained dispatch estimates, multi-snapshot recurrence with recorded source formats, validated relations/documents, bounded and cancelable enrichment, action-adapter policy and local followup drafts.

[WorkplaceBridgeChecks](../WorkplaceBridgeChecks) exercises the production C# operations and the [WebView workplace modules](../../web/js/pages/workplace.js) against isolated fixtures. The [Windows startup harness](../WindowsStartupChecks/README.md) separately drives the actual packaged WinForms/WebView2 host. Cross-compilation and Chromium page checks alone do not establish that the native Windows interface or notifications ran successfully.

`tests/TicketDetailChecks` runs Chromium against synthetic DOM fixtures and a local authenticated document server. It separately verifies native-form preview, one-shot confirmation and observable readback, rejection of stale/unsafe forms, no retry after ambiguous writes and explicit-only downloads. No production portal is used by those checks.
