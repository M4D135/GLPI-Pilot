using System.IO;
using System.Text.Json;
using GlpiPilot.Core;

namespace GlpiPilot.Runtime;

public sealed partial class PilotApplication
{
    private sealed record AnalyticsAssistantPlan(string Id, long Revision, string Rules, ExtractionResult Source, LanguageAssistantRequest Request);
    private AnalyticsAssistantPlan? _analyticsAssistantPlan;
    private (string Id, long Revision, LanguageQueryDraft Draft)? _analyticsAssistantDraft;

    private void RegisterAnalyticsAssistant()
    {
        Register("assistant.config.get", (a, ct) => Task.FromResult<object?>(AnalyticsAssistantProfile(AnalyticsAssistantStorage().Load())));
        Register("assistant.config.save", AnalyticsSaveAssistantConfigAsync);
        Register("assistant.prepare", AnalyticsPrepareAssistantAsync);
        Register("assistant.generate", AnalyticsGenerateAssistantAsync);
        Register("assistant.draft", (a, ct) =>
        {
            AnalyticsCheckRevision(a); var draft = _analyticsAssistantDraft;
            if (draft is null || draft.Value.Revision != Revision || draft.Value.Id != Text(a, "draftId", 96)) throw new InvalidOperationException("La proposition est périmée. Préparez une nouvelle demande.");
            return Task.FromResult<object?>(new { query = draft.Value.Draft.Query.Copy(), draft.Value.Draft.Explanation, draft.Value.Draft.Assumptions, saved = false });
        });
    }
    private LanguageAssistantStore AnalyticsAssistantStorage() => new(DataPath("language-assistant.local.json"));
    private static object AnalyticsAssistantProfile(LanguageAssistantSettings p) => new
    {
        p.Provider, externalEnabled = false, p.Endpoint, p.Model, p.MaximumTickets, p.TimeoutSeconds,
        hasSavedKey = p.ProtectedApiKey.Length > 0, keyEndpoint = p.KeyEndpoint,
        roles = DashboardService.Roles.Select(r => new { r.Key, r.Label }).ToArray(), defaultRoles = LanguageAssistantService.DefaultSummaryRoles,
        note = "Aucun envoi automatique. L’activation externe est demandée pour chaque préparation ; les secrets restent hors configuration partageable."
    };
    private LanguageAssistantSettings AnalyticsAssistantOptions(JsonElement args)
    {
        var saved = AnalyticsAssistantStorage().Load();
        var settings = new LanguageAssistantSettings
        {
            Provider = Text(args, "provider", 20, "local"), ExternalEnabled = Flag(args, "externalEnabled"),
            Endpoint = Text(args, "endpoint", 2000), Model = Text(args, "model", 120),
            MaximumTickets = Number(args, "maximumTickets", 50, 1, 200), TimeoutSeconds = Number(args, "timeoutSeconds", 90, 10, 180),
            ProtectedApiKey = saved.ProtectedApiKey, KeyEndpoint = saved.KeyEndpoint
        };
        settings.Validate(); return settings;
    }
    private Task<object?> AnalyticsSaveAssistantConfigAsync(JsonElement args, CancellationToken ct)
    {
        var settings = AnalyticsAssistantOptions(args); settings.ExternalEnabled = false;
        if (Flag(args, "forgetKey")) { settings.ProtectedApiKey = ""; settings.KeyEndpoint = ""; }
        if (Flag(args, "rememberKey")) LanguageAssistantStore.RememberKey(settings, Text(args, "apiKey", 8000));
        ct.ThrowIfCancellationRequested(); AnalyticsAssistantStorage().Save(settings); _analyticsAssistantPlan = null;
        return Task.FromResult<object?>(AnalyticsAssistantProfile(settings));
    }
    private async Task<object?> AnalyticsPrepareAssistantAsync(JsonElement args, CancellationToken ct)
    {
        AnalyticsCheckRevision(args); var source = RequireResult(); var revision = Revision; var effective = EffectiveSettings;
        var operation = Text(args, "operation", 20, "query"); var prompt = Text(args, "prompt", 2000); var options = AnalyticsAssistantOptions(args);
        var roles = args.TryGetProperty("roles", out var rolesJson) ? Read<string[]>(rolesJson) : LanguageAssistantService.DefaultSummaryRoles;
        var ids = args.TryGetProperty("ticketIds", out var idsJson) && idsJson.ValueKind != JsonValueKind.Null ? Read<string[]>(idsJson) : null;
        var scope = "Toutes les lignes du relevé"; int[] rows;
        if (Text(args, "runId", 96).Length > 0) { var run = RequireAnalyticsRun(args); rows = run.Current.ScopeRows; scope = run.Current.ScopeLabel; }
        else rows = Enumerable.Range(0, source.Rows.Count).ToArray();
        var request = await Jobs.RunAsync("analytics-assistant-prepare", true, token =>
        {
            var context = LanguageAssistantService.BuildContext(source, effective.Dashboard, operation, rows, scope, roles, options.MaximumTickets, ids, token);
            return LanguageAssistantService.Prepare(operation, prompt, options, context);
        }, ct);
        AnalyticsCheckRevision(args); ct.ThrowIfCancellationRequested(); var id = Guid.NewGuid().ToString("N");
        _analyticsAssistantPlan = new(id, revision, SnapshotStore.RulesFingerprint(effective), source, request); _analyticsAssistantDraft = null;
        return new { preparationId = id, revision, operation, preview = request.Preview, request.Context.Scope, request.Context.ScopeRows,
            request.Context.ExtractedAt, request.Context.Complete, tickets = request.Context.Tickets.Count, request.Context.Limitations,
            destination = options.Provider == "local" ? "Moteur local sans réseau" : options.Endpoint, options.Provider, options.ExternalEnabled,
            note = "Préparation uniquement : aucun contenu n’a été envoyé et aucune requête n’a été exécutée." };
    }
    private AnalyticsAssistantPlan RequireAnalyticsAssistantPlan(JsonElement args)
    {
        AnalyticsCheckRevision(args); var plan = _analyticsAssistantPlan;
        if (plan is null || plan.Id != Text(args, "preparationId", 96) || plan.Revision != Revision || !ReferenceEquals(plan.Source, Result)
            || plan.Rules != SnapshotStore.RulesFingerprint(EffectiveSettings)) throw new InvalidOperationException("Cette préparation est périmée. Préparez à nouveau le périmètre exact.");
        return plan;
    }
    private async Task<object?> AnalyticsGenerateAssistantAsync(JsonElement args, CancellationToken ct)
    {
        var plan = RequireAnalyticsAssistantPlan(args); var request = plan.Request;
        if (request.Settings.Provider == "compatible" && (!request.Settings.ExternalEnabled || !Flag(args, "confirmed")))
            throw new InvalidOperationException("Confirmez explicitement l’envoi des seules données affichées à l’endpoint préparé.");
        var response = request.Settings.Provider == "local"
            ? await Jobs.RunAsync("analytics-assistant-local", true, token => LanguageAssistantLocalEngine.Generate(request, token), ct)
            : await LanguageAssistantClient.GenerateAsync(request, Text(args, "apiKey", 8000), ct);
        RequireAnalyticsAssistantPlan(args); ct.ThrowIfCancellationRequested();
        if (request.Operation == "query")
        {
            var draft = LanguageAssistantService.ParseQuery(response, request.Context); var id = Guid.NewGuid().ToString("N");
            _analyticsAssistantDraft = (id, plan.Revision, draft);
            return new { kind = "query", draftId = id, revision = Revision, query = draft.Query, draft.Explanation, draft.Assumptions,
                note = "Proposition validée, non exécutée et non enregistrée. Ouvrez le brouillon dans l’atelier pour prévisualiser ses résultats." };
        }
        var summary = LanguageAssistantService.ParseSummary(response, request.Context);
        return new { kind = "summary", revision = Revision, summary.Title, summary.Limitations,
            claims = summary.Claims.Select(c => new { c.Text, citations = c.Citations.Select(reference => new
            {
                reference.TicketId, reference.Field, reference.Quote,
                rowIndex = request.Context.Tickets.Single(ticket => ticket.Id == reference.TicketId).SourceRow
            }).ToArray() }).ToArray(),
            note = "Les références et extraits sont contrôlés contre les tickets fournis. Ce contrôle ne garantit pas que chaque interprétation du modèle est exacte." };
    }
}
