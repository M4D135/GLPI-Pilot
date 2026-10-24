using System.IO;
using System.Text.Json;
using GlpiPilot.Core;

namespace GlpiPilot.Runtime;

public sealed partial class PilotApplication
{
    private TicketEnrichmentQueue? _workplaceEnrichment;
    private void RegisterWorkplaceEnrichment()
    {
        WorkRegister("workplace.enrichment.get", a => WorkEnrichmentView());
        WorkRegister("workplace.enrichment.prepare", WorkPrepareEnrichment);
        WorkRegister("workplace.enrichment.update", WorkUpdateEnrichment);
        WorkRegister("workplace.enrichment.open", a => { var q = WorkEnrichment(); var key = Text(a, "ticketKey", 64); var ticket = q.Tickets.SingleOrDefault(t => WorkplaceStore.TicketKey(t) == key) ?? throw new InvalidOperationException("Référence absente de la file."); var doc = WorkDocument().Copy(); var p = WorkplaceStore.Remember(doc, WorkValidateReference(ticket), "Ticket " + ticket.Id); WorkSave(doc); return new { ticketKey = p.Key }; });
        Register("workplace.enrichment.run", WorkRunEnrichmentAsync);
    }
    private TicketEnrichmentQueue WorkEnrichment() => _workplaceEnrichment ??= TicketEnrichmentService.Load(DataPath("ticket-enrichment.json"));
    private object WorkEnrichmentView()
    {
        var queue = WorkEnrichment();
        return new { queue.Label, queue.Limit, total = queue.Tickets.Count, revision = Revision,
            rows = queue.Tickets.Select((ticket, i) => new { order = i + 1, ticketKey = WorkplaceStore.TicketKey(ticket), ticket.Id, ticket.Url, outcome = queue.Results.LastOrDefault(result => WorkplaceStore.TicketKey(result.Ticket) == WorkplaceStore.TicketKey(ticket)) }).ToArray() };
    }
    private object WorkPrepareEnrichment(JsonElement a)
    {
        CheckRevision(a); RequireCurrentResult(); var source = RequireResult(); var settings = EffectiveSettings; var candidates = new List<TicketReference>(); var warnings = new List<string>(); var excluded = 0;
        if (a.TryGetProperty("references", out var input))
        {
            var sourceKey = Text(a, "sourceKey", 128);
            if (sourceKey.Length == 0 || sourceKey != SnapshotStore.SourceKey(IsSimulationInstance ? SimulationObservation() : source)) throw new InvalidOperationException("Les références appartiennent à une autre recherche ou à un contexte non vérifié.");
            var refs = Read<List<TicketReference>>(input); if (refs.Count > 1000) throw new InvalidDataException("La file accepte au maximum 1 000 références.");
            foreach (var reference in refs) candidates.Add(WorkValidateReference(reference));
        }
        else
        {
            var indices = WorkArgument<List<int>>(a, "rows"); if (indices.Count > 1000000 || indices.Any(i => i < 0 || i >= source.Rows.Count)) throw new InvalidDataException("La sélection de lignes est invalide.");
            foreach (var index in indices.Distinct().Take(1000))
                try { candidates.Add(WorkValidateReference(IsSimulationInstance ? SimulationReference(index) : TicketReferenceResolver.Resolve(source, source.Rows[index], settings))); }
                catch (InvalidOperationException error) { excluded++; if (warnings.Count < 5) warnings.Add(AppPaths.SafeError(error)); }
            if (indices.Count > 1000) warnings.Add("Préparation limitée aux 1 000 premières lignes sélectionnées.");
        }
        var queue = new TicketEnrichmentQueue { Label = Text(a, "label", 1000, "Lecture ciblée de tickets"), Limit = Number(a, "limit", 20, 1, 100), Tickets = candidates.DistinctBy(WorkplaceStore.TicketKey).ToList() };
        TicketEnrichmentService.Save(DataPath("ticket-enrichment.json"), queue); _workplaceEnrichment = queue;
        Publish("workplace.enrichment.changed", new { total = queue.Tickets.Count }); return new { queue = WorkEnrichmentView(), excluded, warnings };
    }
    private object WorkUpdateEnrichment(JsonElement a)
    {
        var existing = WorkEnrichment(); var queue = new TicketEnrichmentQueue { Label = Text(a, "label", 1000, existing.Label), Limit = Number(a, "limit", existing.Limit, 1, 100), Tickets = existing.Tickets.ToList(), Results = existing.Results.ToList() };
        var priority = Text(a, "priorityKey", 64); if (priority.Length > 0) queue.Tickets = queue.Tickets.OrderBy(t => WorkplaceStore.TicketKey(t) == priority ? 0 : 1).ToList();
        var remove = Text(a, "removeKey", 64); if (remove.Length > 0) { queue.Tickets.RemoveAll(t => WorkplaceStore.TicketKey(t) == remove); queue.Results.RemoveAll(r => WorkplaceStore.TicketKey(r.Ticket) == remove); }
        if (Flag(a, "reset")) { if (!Flag(a, "confirmed")) throw new InvalidOperationException("Confirmez la réinitialisation des résultats de la file."); queue.Results.Clear(); }
        TicketEnrichmentService.Save(DataPath("ticket-enrichment.json"), queue); _workplaceEnrichment = queue; return WorkEnrichmentView();
    }
    private async Task<object?> WorkRunEnrichmentAsync(JsonElement a, CancellationToken ct)
    {
        RequireCurrentResult(); var queue = WorkEnrichment(); var settings = Settings.Copy();
        var readKeys = queue.Results.Where(r => r.State == "Lu").Select(r => WorkplaceStore.TicketKey(r.Ticket)).ToHashSet(StringComparer.Ordinal);
        var planned = TicketEnrichmentService.Plan(queue.Tickets.Where(t => !readKeys.Contains(WorkplaceStore.TicketKey(t))).Select(WorkValidateReference), queue.Limit);
        var outcome = await TicketEnrichmentService.ExecuteAsync(planned,
            (reference, token) => IsSimulationInstance ? Task.FromResult(SimulationDetail(reference)) : Browser.ReadTicketDetailsAsync(reference, settings, null, token), detail => WorkAcceptDetail(detail),
            new WorkProgress<TicketEnrichmentOutcome>(result =>
            {
                var key = WorkplaceStore.TicketKey(result.Ticket); queue.Results.RemoveAll(r => WorkplaceStore.TicketKey(r.Ticket) == key); queue.Results.Add(result);
                TicketEnrichmentService.Save(DataPath("ticket-enrichment.json"), queue);
                Publish("workplace.enrichment.progress", new { ticketKey = key, outcome = result, completed = queue.Results.Count(r => r.State == "Lu"), total = queue.Tickets.Count });
            }), ct);
        return new { queue = WorkEnrichmentView(), interrupted = ct.IsCancellationRequested, attempted = outcome.Count(r => r.State != "Non lu") };
    }
}
