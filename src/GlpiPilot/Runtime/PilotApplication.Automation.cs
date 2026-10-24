using System.IO;
using System.Text.Json;
using GlpiPilot.Core;

namespace GlpiPilot.Runtime;

public sealed partial class PilotApplication
{
    private DateTimeOffset? _nextAutomatic;
    private string _automaticPolicyStamp = "";
    private Func<bool> _networkAvailable = System.Net.NetworkInformation.NetworkInterface.GetIsNetworkAvailable;
    private void RegisterAutomationOperations()
    {
        Register("automation.get", (_, _) => Task.FromResult<object?>(new { settings = Settings.Automation, next = _nextAutomatic, health = _health.Entries, alerts = _health.Alerts(Settings.Sources, Settings.Automation, DateTimeOffset.Now) }));
        Register("automation.save", (a, _) => { var next = Read<AutomationSettings>(a); AutomationPolicy.Times(next.FixedTimes); AutomationPolicy.Days(next.WeekDays); AutomationPolicy.ValidateRecipe(next.Steps); next.Normalize(); Settings.Automation = next; SaveSettings(); _nextAutomatic = AutomationPolicy.Next(next, DateTimeOffset.Now); _automaticPolicyStamp = JsonSerializer.Serialize(next, Json); return Task.FromResult<object?>(new { settings = next, next = _nextAutomatic }); });
        Register("automation.recipe", async (_, ct) => await RunRecipeForRuntimeAsync(ct).ConfigureAwait(false));
        Register("automation.tick", RunAutomaticTickAsync);
        Register("alerts.get", (_, _) => Task.FromResult<object?>(new { rules = _alerts.Document.Rules, incidents = _alerts.Document.Incidents.OrderByDescending(i => i.LastObserved), health = _health.Alerts(Settings.Sources, Settings.Automation, DateTimeOffset.Now) }));
        Register("alerts.rules", (a, _) => { _alerts.SaveRules(Read<List<AlertRule>>(a.GetProperty("rules"))); return Task.FromResult<object?>(_alerts.Document.Rules); });
        Register("alerts.evaluate", (_, ct) => { EvaluateAlerts(ct); return Task.FromResult<object?>(_alerts.Document.Incidents); });
        Register("alerts.acknowledge", (a, _) => { _alerts.Acknowledge(Text(a, "id", 80)); return Task.FromResult<object?>(new { saved = true }); });
        Register("alerts.snooze", (a, _) => { _alerts.Snooze(Text(a, "id", 80), TimeSpan.FromMinutes(Number(a, "minutes", 60, 5, 10080))); return Task.FromResult<object?>(new { saved = true }); });
        Register("alerts.resolve", (a, _) => { _alerts.Resolve(Text(a, "id", 80), Text(a, "reason", 2000)); return Task.FromResult<object?>(new { saved = true }); });
        Register("alerts.rows", (a, _) => { var current = RequireResult(); var incident = _alerts.Document.Incidents.SingleOrDefault(i => i.Id == Text(a, "id", 80)) ?? throw new InvalidDataException("Alerte introuvable."); if (incident.RunId != current.Provenance.RunId) throw new InvalidOperationException("Chargez le relevé associé à cette alerte."); return Task.FromResult<object?>(new { rows = incident.Rows, revision = Revision, contributors = incident.ContributorIds, added = incident.NewContributorIds }); });
    }
    private void EvaluateAlerts(CancellationToken ct)
    {
        if (Result is null || IsHistorical || IsLocalResult(Result) && !IsSimulationInstance) return;
        var data = IsSimulationInstance ? SimulationObservation() : Result;
        var settings = EffectiveSettings; var snapshot = DashboardService.Calculate(data, settings.Dashboard, ct: ct); QueryService.AppendDashboardQueries(snapshot, data, settings.Dashboard, settings.Queries, ct);
        _alerts.Evaluate(data, snapshot, ct, settings); Publish("alerts.changed", new { active = _alerts.Document.Incidents.Count(i => i.Active), simulated = IsSimulationInstance });
    }
    public async Task TickAsync(CancellationToken ct = default)
    {
        if (_disposed || IsBusy || SessionLocked || EditorOpen) return;
        try { using var empty = JsonDocument.Parse("{}"); await ExecuteWhenIdleAsync("automation.tick", empty.RootElement, ct).ConfigureAwait(false); }
        catch (Exception error) when (error is not OperationCanceledException && !_disposed) { Log("Programmation : " + AppPaths.SafeError(error)); }
    }
    private async Task<object?> RunAutomaticTickAsync(JsonElement args, CancellationToken ct)
    {
        if (SessionLocked || EditorOpen) return new { skipped = true };
        if (_handlers.ContainsKey("workplace.reminders.check"))
        {
            try { await _handlers["workplace.reminders.check"](args, ct).ConfigureAwait(false); }
            catch (Exception error) when (error is not OperationCanceledException) { Log("Rappels : " + AppPaths.SafeError(error)); }
        }
        if (_handlers.TryGetValue("library.tick", out var libraryTick))
            await libraryTick(args, ct).ConfigureAwait(false);
        var policy = Settings.Automation.Copy(); var stamp = JsonSerializer.Serialize(policy, Json);
        if (_automaticPolicyStamp != stamp) { _nextAutomatic = null; _automaticPolicyStamp = stamp; }
        if (!policy.ScheduledExtraction || AutomationPolicy.IsQuiet(policy, DateTimeOffset.Now) || SessionLocked || EditorOpen) return new { skipped = true };
        _nextAutomatic ??= AutomationPolicy.Next(policy, DateTimeOffset.Now);
        if (_nextAutomatic > DateTimeOffset.Now) return new { skipped = true };
        if (!_networkAvailable()) return new { skipped = true };
        _nextAutomatic = AutomationPolicy.Next(policy, DateTimeOffset.Now);
        ct.ThrowIfCancellationRequested(); var operation = policy.UseRecipe ? "automation.recipe" : policy.RunCampaign ? "collect.campaign" : "collect.run";
        CurrentOperation = operation; Publish("operation.state", new { busy = true, operation });
        var result = await _handlers[operation](args, ct).ConfigureAwait(false);
        return new { ran = true, result };
    }
    public void ResumeAutomaticSchedule()
    {
        if (_simulation is { } simulation) { simulation.ResumeAutomaticSchedule(); return; }
        lock (_executionAdmission)
        {
            if (_disposed || _pendingExecutions.Count > 0) return;
            if (Settings.Automation.ResumeAfterReconnect && Settings.Automation.ScheduledExtraction && _nextAutomatic < DateTimeOffset.Now) _nextAutomatic = DateTimeOffset.Now;
        }
    }
    private async Task<object?> RunRecipeForRuntimeAsync(CancellationToken ct)
    {
        AutomationPolicy.ValidateRecipe(Settings.Automation.Steps); var saved = Settings.Copy(); var outputs = new List<object>();
        foreach (var step in saved.Automation.Steps)
        {
            ct.ThrowIfCancellationRequested();
            if (!AutomationPolicy.ShouldRun(step, step.Kind == "extract" ? null : Result)) { Log("Recette : " + step.Kind + " ignorée."); continue; }
            Log("Recette : " + step.Kind + " en cours.");
            switch (step.Kind)
            {
                case "extract": var raw = saved.Copy(); raw.Cities.Enabled = false; raw.AutoSaveXlsx = false; await RunCollectionAsync(raw, false, ct).ConfigureAwait(false); break;
                case "quality": outputs.Add(DataQualityService.Analyze(RequireResult(), EffectiveSettings.Dashboard, ct)); break;
                case "enrich":
                    var interpretation = IsSimulationInstance ? _simulationLiveSettings ?? saved : saved;
                    var cities = IsSimulationInstance ? SimulationEnrich(_simulationLiveData!, interpretation, ct)
                        : CityDetectionService.Enrich(RequireResult(), saved.Cities, saved.Dashboard, ct);
                    SetResult(cities.Data, cities, context: interpretation); break;
                case "archive": await ArchiveActiveAsync(ct).ConfigureAwait(false); break;
                case "csv": case "xlsx": var path = Path.Combine(saved.ExportFolder, "GLPI-Recette-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + "-" + Guid.NewGuid().ToString("N")[..6] + "." + step.Kind); outputs.Add(step.Kind == "csv" ? ExportService.WriteCsv(RequireResult(), path, ct) : ExportService.WriteXlsx(RequireResult(), path, ct)); break;
                case "alerts": EvaluateAlerts(ct); break;
            }
            Log("Recette : " + step.Kind + " terminée.");
        }
        return new { session = SessionState(), outputs };
    }
}
